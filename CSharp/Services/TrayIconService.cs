using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SmartPowerManager
{
  public sealed class TrayIconService : IDisposable
  {
    public const uint WM_TRAYICON = 0x8001;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;
    private const int ID_OPEN = 1001;
    private const int ID_EXIT = 1002;

    private readonly IntPtr _hwnd;
    private readonly uint _iconId;
    private IntPtr _iconHandle;
    private bool _isVisible;

    public event Action? OpenMainWindowRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public TrayIconService(IntPtr hwnd, uint iconId = 1)
    {
      _hwnd = hwnd;
      _iconId = iconId;
      _iconHandle = LoadAppIcon();
    }

    public void Show()
    {
      if (_isVisible) return;

      var data = CreateNotifyData();
      Shell_NotifyIcon(NIM_ADD, ref data);
      _isVisible = true;
    }

    public void Hide()
    {
      if (!_isVisible) return;

      var data = CreateNotifyData();
      Shell_NotifyIcon(NIM_DELETE, ref data);
      _isVisible = false;
    }

    public void ProcessMessage(IntPtr lParam)
    {
      var msg = (uint)lParam.ToInt64();
      if (msg == WM_LBUTTONDBLCLK)
      {
        OpenMainWindowRequested?.Invoke();
        return;
      }

      if (msg == WM_RBUTTONUP)
        ShowContextMenu();
    }

    private void ShowContextMenu()
    {
      IntPtr menu = CreatePopupMenu();
      AppendMenu(menu, MF_STRING, ID_OPEN, Strings.Get("Tray_OpenSettings"));
      AppendMenu(menu, MF_SEPARATOR, 0, null);
      AppendMenu(menu, MF_STRING, ID_EXIT, Strings.Get("Tray_Exit"));

      GetCursorPos(out POINT pt);
      SetForegroundWindow(_hwnd);

      uint cmd = TrackPopupMenu(
        menu,
        TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD,
        pt.X,
        pt.Y,
        0,
        _hwnd,
        IntPtr.Zero);

      DestroyMenu(menu);

      if (cmd == ID_OPEN)
        OpenSettingsRequested?.Invoke();
      else if (cmd == ID_EXIT)
        ExitRequested?.Invoke();
    }

    private NOTIFYICONDATA CreateNotifyData()
    {
      return new NOTIFYICONDATA
      {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = _iconId,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _iconHandle,
        szTip = Strings.Get("AppName")
      };
    }

    private static IntPtr LoadAppIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.ico");
        if (File.Exists(iconPath))
        {
            IntPtr icon = PInvokeHelper.LoadImageIcon(iconPath);
            if (icon != IntPtr.Zero)
                return icon;
        }

        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        ushort index = 0;
        IntPtr extracted = ExtractAssociatedIcon(IntPtr.Zero, exePath, ref index);
        return extracted != IntPtr.Zero ? extracted : IntPtr.Zero;
    }

    public void Dispose()
    {
      Hide();
      if (_iconHandle != IntPtr.Zero)
      {
        DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
      }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
      public uint cbSize;
      public IntPtr hWnd;
      public uint uID;
      public uint uFlags;
      public uint uCallbackMessage;
      public IntPtr hIcon;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
      public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
      public int X;
      public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, string pszIconPath, ref ushort piIconIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
      IntPtr hMenu,
      uint uFlags,
      int x,
      int y,
      int nReserved,
      IntPtr hWnd,
      IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
  }
}
