using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinUiShared;

/// <summary>
/// スリープ復帰・画面復帰・セッション解除など、コア設定が OS によりリセットされうるイベントを監視する非表示ウィンドウ。
/// 専用 STA メッセージループで動作し、UI スレッドの詰まりに依存しない。
/// </summary>
public sealed class SystemEventWindow : IDisposable
{
    // GUID_CONSOLE_DISPLAY_STATE（末尾は 12 hex。誤った 13 桁だと型初期化失敗で監視全体が無効になる）
    private static readonly Guid ConsoleDisplayStateGuid =
        new("6fe69556-704a-47a0-aa35-2f285d73bf87");

    // GUID_MONITOR_POWER_ON（Modern Standby 等で CONSOLE_DISPLAY_STATE が来ない機種の補完）
    private static readonly Guid MonitorPowerOnGuid =
        new("02731015-4510-4526-99e6-e5a17ebd1aea");

    // GUID_LIDSWITCH_STATE_CHANGE（蓋を開いたとき）
    private static readonly Guid LidSwitchStateChangeGuid =
        new("ba3e0f4d-b817-4094-a2d1-d56379e6d8b1");

    // GUID_SESSION_DISPLAY_STATUS（セッションの画面 On）
    private static readonly Guid SessionDisplayStatusGuid =
        new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_APP = 0x8000;
    private const uint WM_APP_SHUTDOWN = WM_APP + 1;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const int PBT_APMQUERYSUSPEND = 0x0;
    private const int PBT_APMSUSPEND = 0x4;
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
    private static readonly object ClassGate = new();
    private static readonly HashSet<string> RegisteredClasses = new(StringComparer.Ordinal);
    private static IntPtr _hInstance;

    private readonly string _windowClassName;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _notifyGate = new();
    private readonly Thread _thread;

    private IntPtr _hwnd;
    private IntPtr _displayStateNotification = IntPtr.Zero;
    private IntPtr _monitorPowerNotification = IntPtr.Zero;
    private IntPtr _lidSwitchNotification = IntPtr.Zero;
    private IntPtr _sessionDisplayNotification = IntPtr.Zero;
    private GCHandle _selfHandle;
    private Exception? _initError;
    private bool _disposed;
    private bool _coalesceWindowOpen;
    private bool _coalesceTrailingPending;

    public event Action? SystemDisplayStateChanged;
    public event Action? SystemSuspending;

    public bool IsAlive => _hwnd != IntPtr.Zero && IsWindow(_hwnd);

    public SystemEventWindow(string windowClassName)
    {
        if (string.IsNullOrWhiteSpace(windowClassName))
            throw new ArgumentException("Window class name is required.", nameof(windowClassName));

        _windowClassName = windowClassName;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = windowClassName
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
            EnsureClassRegistered(_windowClassName);

            _hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                _windowClassName,
                _windowClassName,
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

            _displayStateNotification = RegisterRequiredPowerSetting(ConsoleDisplayStateGuid, "CONSOLE_DISPLAY_STATE");
            _monitorPowerNotification = RegisterOptionalPowerSetting(MonitorPowerOnGuid, "MONITOR_POWER_ON");
            _lidSwitchNotification = RegisterOptionalPowerSetting(LidSwitchStateChangeGuid, "LIDSWITCH_STATE_CHANGE");
            _sessionDisplayNotification = RegisterOptionalPowerSetting(SessionDisplayStatusGuid, "SESSION_DISPLAY_STATUS");
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

    private IntPtr RegisterRequiredPowerSetting(Guid guid, string name)
    {
        Guid copy = guid;
        IntPtr handle = RegisterPowerSettingNotification(_hwnd, ref copy, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"RegisterPowerSettingNotification({name}) failed: {Marshal.GetLastWin32Error()}");
        }

        return handle;
    }

    private IntPtr RegisterOptionalPowerSetting(Guid guid, string name)
    {
        Guid copy = guid;
        IntPtr handle = RegisterPowerSettingNotification(_hwnd, ref copy, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (handle == IntPtr.Zero)
        {
            Debug.WriteLine(
                $"RegisterPowerSettingNotification({name}) failed: {Marshal.GetLastWin32Error()}");
        }

        return handle;
    }

    private static void EnsureClassRegistered(string windowClassName)
    {
        lock (ClassGate)
        {
            if (RegisteredClasses.Contains(windowClassName))
                return;

            _hInstance = GetModuleHandle(IntPtr.Zero);

            var wc = new WNDCLASSW
            {
                lpfnWndProc = StaticWndProc,
                hInstance = _hInstance,
                lpszClassName = windowClassName
            };

            ushort atom = RegisterClassW(ref wc);
            if (atom == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                    throw new InvalidOperationException($"RegisterClass failed: {error}");
            }

            RegisteredClasses.Add(windowClassName);
        }
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr userData = GetWindowLongPtr(hWnd, GWLP_USERDATA);
        SystemEventWindow? target = userData != IntPtr.Zero
            ? GCHandle.FromIntPtr(userData).Target as SystemEventWindow
            : null;

        // OS がスリープ等で WM_CLOSE を投げても監視窓は落とさない
        if (msg == WM_CLOSE)
            return IntPtr.Zero;

        if (msg == WM_APP_SHUTDOWN && target != null)
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

        if (target != null)
        {
            if (ShouldNotifySuspending(msg, wParam, lParam))
                target.RaiseSystemSuspending();

            if (ShouldNotifyDisplayStateChanged(msg, wParam, lParam))
                target.RequestCoalescedNotify();
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void UnregisterNotificationsOnly()
    {
        UnregisterPowerHandle(ref _lidSwitchNotification);
        UnregisterPowerHandle(ref _sessionDisplayNotification);
        UnregisterPowerHandle(ref _monitorPowerNotification);
        UnregisterPowerHandle(ref _displayStateNotification);

        if (_hwnd != IntPtr.Zero)
            WTSUnRegisterSessionNotification(_hwnd);
    }

    private static void UnregisterPowerHandle(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        UnregisterPowerSettingNotification(handle);
        handle = IntPtr.Zero;
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

    private void RaiseSystemSuspending()
    {
        try
        {
            SystemSuspending?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SystemSuspending handler failed: {ex.Message}");
        }
    }

    private static bool ShouldNotifySuspending(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_POWERBROADCAST && IsPowerSuspendMessage(wParam))
            return true;

        if (msg == WM_POWERBROADCAST && IsLidClosedMessage(wParam, lParam))
            return true;

        return false;
    }

    private static bool ShouldNotifyDisplayStateChanged(uint msg, IntPtr wParam, IntPtr lParam)
    {
        return msg switch
        {
            WM_DISPLAYCHANGE => true,
            WM_POWERBROADCAST when IsPowerResumeMessage(wParam) => true,
            WM_POWERBROADCAST when IsDisplayPowerOnMessage(wParam, lParam) => true,
            WM_POWERBROADCAST when IsLidOpenedMessage(wParam, lParam) => true,
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

    private static bool IsPowerSuspendMessage(IntPtr wParam)
    {
        int eventType = wParam.ToInt32();
        return eventType is PBT_APMSUSPEND or PBT_APMQUERYSUSPEND;
    }

    private static bool TryReadPowerSetting(IntPtr wParam, IntPtr lParam, out Guid guid, out byte state)
    {
        guid = default;
        state = 0;
        if (wParam.ToInt32() != PBT_POWERSETTINGCHANGE || lParam == IntPtr.Zero)
            return false;

        var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
        if (setting.DataLength < 1)
            return false;

        guid = setting.PowerSetting;
        int offset = Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32();
        state = Marshal.ReadByte(lParam, offset);
        return true;
    }

    private static bool IsDisplayPowerOnMessage(IntPtr wParam, IntPtr lParam)
    {
        if (!TryReadPowerSetting(wParam, lParam, out Guid guid, out byte state))
            return false;

        if (guid != ConsoleDisplayStateGuid
            && guid != MonitorPowerOnGuid
            && guid != SessionDisplayStatusGuid)
            return false;

        return state == 1;
    }

    private static bool IsLidOpenedMessage(IntPtr wParam, IntPtr lParam)
    {
        if (!TryReadPowerSetting(wParam, lParam, out Guid guid, out byte state))
            return false;

        return guid == LidSwitchStateChangeGuid && state == 1;
    }

    private static bool IsLidClosedMessage(IntPtr wParam, IntPtr lParam)
    {
        if (!TryReadPowerSetting(wParam, lParam, out Guid guid, out byte state))
            return false;

        return guid == LidSwitchStateChangeGuid && state == 0;
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
            PostMessageW(hwnd, WM_APP_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
    }

    private void CleanupNativeResources()
    {
        UnregisterNotificationsOnly();

        if (_hwnd != IntPtr.Zero)
        {
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
