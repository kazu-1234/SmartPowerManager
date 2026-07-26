using System;
using System.Runtime.InteropServices;

namespace SmartPowerManager.Services;

/// <summary>
/// スリープ復帰・画面復帰・セッション解除など、コア機能が OS により不安定になりうるイベントを監視する非表示ウィンドウ。
/// タスクトレイとは独立して常に生成する（BlueShift / ADM 同等）。
/// </summary>
internal sealed class SystemEventWindow : IDisposable
{
    // GUID_CONSOLE_DISPLAY_STATE（末尾は 12 hex）
    private static readonly Guid ConsoleDisplayStateGuid =
        new("6fe69556-704a-47a0-aa35-2f285d73bf87");

    private const string WindowClassName = "SmartPowerManagerSystemEventWindow_v1";
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

    private static readonly WndProcDelegate StaticWndProc = WindowProc;
    private static bool _classRegistered;
    private static IntPtr _hInstance;

    private IntPtr _hwnd;
    private IntPtr _displayStateNotification = IntPtr.Zero;
    private GCHandle _selfHandle;

    public event Action? SystemDisplayStateChanged;

    public SystemEventWindow()
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
        {
            int error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"WTSRegisterSessionNotification failed: {error}");
        }

        Guid displayGuid = ConsoleDisplayStateGuid;
        _displayStateNotification = RegisterPowerSettingNotification(
            _hwnd,
            ref displayGuid,
            DEVICE_NOTIFY_WINDOW_HANDLE);
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

        if (target != null && ShouldNotifyDisplayStateChanged(msg, wParam, lParam))
            target.SystemDisplayStateChanged?.Invoke();

        return DefWindowProcW(hWnd, msg, wParam, lParam);
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
        if (setting.PowerSetting != ConsoleDisplayStateGuid || setting.DataLength < 1)
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

    public void Dispose()
    {
        if (_displayStateNotification != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_displayStateNotification);
            _displayStateNotification = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            WTSUnRegisterSessionNotification(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
