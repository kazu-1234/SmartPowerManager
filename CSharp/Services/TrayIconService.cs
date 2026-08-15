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
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIN_SELECT = 0x0400;
    private const uint NIN_KEYSELECT = 0x0401;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIIF_INFO = 0x00000001;
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
      AddIcon();
    }

    public void Hide()
    {
      if (!_isVisible) return;

      var data = CreateNotifyData();
      Shell_NotifyIcon(NIM_DELETE, ref data);
      _isVisible = false;
    }

    /// <summary>explorer 再生成やスリープ復帰後にアイコンを付け直す。</summary>
    public void ReAdd()
    {
      var data = CreateNotifyData();
      Shell_NotifyIcon(NIM_DELETE, ref data);
      _isVisible = false;
      AddIcon();
    }

    /// <summary>起動完了など、トレイ付近のバルーン通知。</summary>
    public void ShowBalloon(string title, string message)
    {
      if (!_isVisible)
        return;

      var data = CreateNotifyData();
      data.uFlags = NIF_INFO;
      data.szInfoTitle = Truncate(title, 63);
      data.szInfo = Truncate(message, 255);
      data.dwInfoFlags = NIIF_INFO;
      Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private static string Truncate(string value, int maxChars)
    {
      if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        return value ?? string.Empty;
      return value[..maxChars];
    }

    private void AddIcon()
    {
      var data = CreateNotifyData();
      if (!Shell_NotifyIcon(NIM_ADD, ref data))
      {
        Shell_NotifyIcon(NIM_DELETE, ref data);
        Shell_NotifyIcon(NIM_ADD, ref data);
      }

      data.uVersion = NOTIFYICON_VERSION_4;
      Shell_NotifyIcon(NIM_SETVERSION, ref data);
      _isVisible = true;
    }

    public void ProcessMessage(IntPtr lParam)
    {
      uint msg = (uint)(lParam.ToInt64() & 0xFFFF);
      if (msg == WM_LBUTTONDBLCLK || msg == NIN_SELECT || msg == NIN_KEYSELECT)
      {
        OpenMainWindowRequested?.Invoke();
        return;
      }

      if (msg == WM_RBUTTONUP || msg == WM_CONTEXTMENU)
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
        szTip = Strings.Get("AppName"),
        szInfo = string.Empty,
        szInfoTitle = string.Empty
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
      public uint dwState;
      public uint dwStateMask;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
      public string szInfo;
      public uint uVersion;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
      public string szInfoTitle;
      public uint dwInfoFlags;
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
