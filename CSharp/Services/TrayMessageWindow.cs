using System;
using System.Runtime.InteropServices;

namespace SmartPowerManager
{
    /// <summary>
    /// タスクトレイ通知専用の非表示ウィンドウ。
    /// WinUI のメインウィンドウへ WndProc を差し替えないことで、白画面を防ぐ。
    /// </summary>
    internal sealed class TrayMessageWindow : IDisposable
    {
        private const string WindowClassName = "SmartPowerManager_TrayMessageWindow_v1";
        private const int GWLP_USERDATA = -21;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

        private static readonly WndProcDelegate StaticWndProc = WindowProc;
        private static bool _classRegistered;
        private static IntPtr _hInstance;

        private IntPtr _hwnd;
        private GCHandle _selfHandle;
        private TrayIconService? _trayIconService;

        public bool IsCreated => _hwnd != IntPtr.Zero;

        public TrayIconService TrayIcon =>
            _trayIconService ?? throw new InvalidOperationException("Tray icon is not initialized.");

        public TrayMessageWindow()
        {
            EnsureClassRegistered();

            _hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                WindowClassName,
                "SmartPowerManagerTray",
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

            _trayIconService = new TrayIconService(_hwnd);
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
            if (msg == TrayIconService.WM_TRAYICON)
            {
                IntPtr userData = GetWindowLongPtr(hWnd, GWLP_USERDATA);
                if (userData != IntPtr.Zero)
                {
                    var target = GCHandle.FromIntPtr(userData).Target as TrayMessageWindow;
                    target?._trayIconService?.ProcessMessage(lParam);
                }

                return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            _trayIconService?.Dispose();
            _trayIconService = null;

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
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
