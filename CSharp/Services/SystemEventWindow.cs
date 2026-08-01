using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPowerManager.Services
{
    /// <summary>
    /// スリープ復帰・画面復帰・セッション解除など、ガンマが OS によりリセットされうるイベントを監視する非表示ウィンドウ。
    /// 専用 STA メッセージループで動作し、UI スレッドの詰まりに依存しない。
    /// </summary>
    internal sealed class SystemEventWindow : IDisposable
    {
        // GUID_CONSOLE_DISPLAY_STATE（末尾は 12 hex。誤った 13 桁だと型初期化失敗で監視全体が無効になる）
        private static readonly Guid ConsoleDisplayStateGuid =
            new("6fe69556-704a-47a0-aa35-2f285d73bf87");

        // GUID_MONITOR_POWER_ON（Modern Standby 等で CONSOLE_DISPLAY_STATE が来ない機種の補完）
        private static readonly Guid MonitorPowerOnGuid =
            new("02731015-4510-4526-99e6-e5a17ebd1aea");

        private const string WindowClassName = "SmartPowerManagerSystemEventWindow_v2";
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_DISPLAYCHANGE = 0x007E;
        private const uint WM_POWERBROADCAST = 0x0218;
        private const uint WM_WTSSESSION_CHANGE = 0x02B1;
        private const int PBT_APMRESUMEAUTOMATIC = 0x12;
        private const int PBT_APMRESUMECRITICAL = 0x6;
        private const int PBT_APMRESUMESUSPEND = 0x7;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int WTS_CONSOLE_CONNECT = 0x1;
        private const int WTS_REMOTE_CONNECT = 0x3;
        private const int WTS_SESSION_UNLOCK = 0x8;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        private const int GWLP_USERDATA = -21;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        private const int CoalesceMs = 300;
        private const int ReadyTimeoutMs = 10000;

        private static readonly WndProcDelegate StaticWndProc = WindowProc;
        private static bool _classRegistered;
        private static IntPtr _hInstance;

        private readonly ManualResetEventSlim _ready = new(false);
        private readonly object _notifyGate = new();
        private readonly Thread _thread;

        private IntPtr _hwnd;
        private IntPtr _displayStateNotification = IntPtr.Zero;
        private IntPtr _monitorPowerNotification = IntPtr.Zero;
        private GCHandle _selfHandle;
        private Exception? _initError;
        private bool _disposed;
        private bool _coalesceWindowOpen;
        private bool _coalesceTrailingPending;

        public event Action? SystemDisplayStateChanged;

        public SystemEventWindow()
        {
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "SmartPowerManagerSystemEvent"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_ready.Wait(ReadyTimeoutMs))
            {
                RequestShutdown();
                throw new TimeoutException("SystemEventWindow message thread failed to start.");
            }

            if (_initError != null)
            {
                RequestShutdown();
                _thread.Join(2000);
                throw new InvalidOperationException(
                    "SystemEventWindow initialization failed.",
                    _initError);
            }
        }

        private void ThreadMain()
        {
            try
            {
                EnsureClassRegistered();

                _hwnd = CreateWindowEx(
                    WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                    WindowClassName,
                    "SmartPowerManagerSystemEvent",
                    WS_POPUP,
                    0, 0, 1, 1,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    _hInstance,
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                    throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

                _selfHandle = GCHandle.Alloc(this);
                SetWindowLongPtr(_hwnd, GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));

                if (!WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION))
                    throw new InvalidOperationException(
                        $"WTSRegisterSessionNotification failed: {Marshal.GetLastWin32Error()}");

                Guid displayGuid = ConsoleDisplayStateGuid;
                _displayStateNotification = RegisterPowerSettingNotification(
                    _hwnd,
                    ref displayGuid,
                    DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_displayStateNotification == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"RegisterPowerSettingNotification(CONSOLE_DISPLAY_STATE) failed: {Marshal.GetLastWin32Error()}");

                Guid monitorGuid = MonitorPowerOnGuid;
                _monitorPowerNotification = RegisterPowerSettingNotification(
                    _hwnd,
                    ref monitorGuid,
                    DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_monitorPowerNotification == IntPtr.Zero)
                {
                    Debug.WriteLine(
                        $"RegisterPowerSettingNotification(MONITOR_POWER_ON) failed: {Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                _initError = ex;
                CleanupNativeResources();
                _ready.Set();
                return;
            }

            _ready.Set();

            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            CleanupNativeResources();
        }

        private static void EnsureClassRegistered()
        {
            if (_classRegistered)
                return;

            _hInstance = GetModuleHandle(IntPtr.Zero);

            var wc = new WNDCLASSW
            {
                lpfnWndProc = StaticWndProc,
                hInstance = _hInstance,
                lpszClassName = WindowClassName
            };

            ushort atom = RegisterClassW(ref wc);
            if (atom == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                    throw new InvalidOperationException($"RegisterClass failed: {error}");
            }

            _classRegistered = true;
        }

        private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            IntPtr userData = GetWindowLongPtr(hWnd, GWLP_USERDATA);
            SystemEventWindow? target = userData != IntPtr.Zero
                ? GCHandle.FromIntPtr(userData).Target as SystemEventWindow
                : null;

            if (msg == WM_CLOSE && target != null)
            {
                target.UnregisterNotificationsOnly();
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            }

            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            if (target != null && ShouldNotifyDisplayStateChanged(msg, wParam, lParam))
                target.RequestCoalescedNotify();

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private void UnregisterNotificationsOnly()
        {
            if (_monitorPowerNotification != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_monitorPowerNotification);
                _monitorPowerNotification = IntPtr.Zero;
            }

            if (_displayStateNotification != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_displayStateNotification);
                _displayStateNotification = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
                WTSUnRegisterSessionNotification(_hwnd);
        }

        private void RequestCoalescedNotify()
        {
            bool fireNow = false;
            bool scheduleTrailing = false;

            lock (_notifyGate)
            {
                if (!_coalesceWindowOpen)
                {
                    _coalesceWindowOpen = true;
                    fireNow = true;
                    scheduleTrailing = true;
                }
                else
                {
                    _coalesceTrailingPending = true;
                }
            }

            if (fireNow)
                RaiseSystemDisplayStateChanged();

            if (scheduleTrailing)
            {
                Task.Delay(CoalesceMs).ContinueWith(
                    _ =>
                    {
                        bool fireTrailing;
                        lock (_notifyGate)
                        {
                            fireTrailing = _coalesceTrailingPending;
                            _coalesceTrailingPending = false;
                            _coalesceWindowOpen = false;
                        }

                        if (fireTrailing)
                            RaiseSystemDisplayStateChanged();
                    },
                    TaskScheduler.Default);
            }
        }

        private void RaiseSystemDisplayStateChanged()
        {
            try
            {
                SystemDisplayStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SystemDisplayStateChanged handler failed: {ex.Message}");
            }
        }

        private static bool ShouldNotifyDisplayStateChanged(uint msg, IntPtr wParam, IntPtr lParam)
        {
            return msg switch
            {
                WM_DISPLAYCHANGE => true,
                WM_POWERBROADCAST when IsPowerResumeMessage(wParam) => true,
                WM_POWERBROADCAST when IsDisplayPowerOnMessage(wParam, lParam) => true,
                WM_WTSSESSION_CHANGE when IsSessionResumeMessage(wParam) => true,
                _ => false
            };
        }

        private static bool IsPowerResumeMessage(IntPtr wParam)
        {
            int eventType = wParam.ToInt32();
            return eventType is PBT_APMRESUMESUSPEND
                or PBT_APMRESUMEAUTOMATIC
                or PBT_APMRESUMECRITICAL;
        }

        private static bool IsDisplayPowerOnMessage(IntPtr wParam, IntPtr lParam)
        {
            if (wParam.ToInt32() != PBT_POWERSETTINGCHANGE || lParam == IntPtr.Zero)
                return false;

            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.DataLength < 1)
                return false;

            if (setting.PowerSetting != ConsoleDisplayStateGuid
                && setting.PowerSetting != MonitorPowerOnGuid)
                return false;

            int offset = Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32();
            byte state = Marshal.ReadByte(lParam, offset);
            return state == 1;
        }

        private static bool IsSessionResumeMessage(IntPtr wParam)
        {
            int eventType = wParam.ToInt32();
            return eventType is WTS_CONSOLE_CONNECT
                or WTS_REMOTE_CONNECT
                or WTS_SESSION_UNLOCK;
        }

        private void RequestShutdown()
        {
            IntPtr hwnd = _hwnd;
            if (hwnd != IntPtr.Zero)
                PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private void CleanupNativeResources()
        {
            if (_monitorPowerNotification != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_monitorPowerNotification);
                _monitorPowerNotification = IntPtr.Zero;
            }

            if (_displayStateNotification != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_displayStateNotification);
                _displayStateNotification = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
            {
                WTSUnRegisterSessionNotification(_hwnd);
                // DestroyWindow はメッセージループスレッド上で既に行われている場合がある
                if (IsWindow(_hwnd))
                    DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            RequestShutdown();
            if (_thread.IsAlive)
                _thread.Join(5000);

            _ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSW
        {
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const int NOTIFY_FOR_THIS_SESSION = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(
            IntPtr hRecipient,
            ref Guid powerSettingGuid,
            int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
        }

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : GetWindowLongPtr32(hWnd, nIndex);
        }
    }
}
