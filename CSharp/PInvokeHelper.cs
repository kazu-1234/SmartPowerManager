using System.Runtime.InteropServices;

namespace SmartPowerManager;

internal static class PInvokeHelper
{
    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    public static IntPtr LoadImageIcon(string path) =>
        LoadImage(IntPtr.Zero, path, 1, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);

    public static void SendMessageIcon(IntPtr hwnd, IntPtr hIcon)
    {
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIcon);
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIcon);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
