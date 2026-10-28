using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinUiShared
{
    /// <summary>
    /// アプリがトレイメニューに足す 1 項目。Text が空なら区切り線として扱う。
    /// </summary>
    /// <param name="Text">表示文言（空文字で区切り線）。</param>
    /// <param name="OnClick">選ばれたときの処理（トレイ用ウィンドウのスレッド＝UI スレッドで呼ばれる）。</param>
    /// <param name="IsChecked">チェックマークを付けるか（ON/OFF 切り替え項目用）。</param>
    /// <param name="IsEnabled">false なら灰色表示で選べない。</param>
    public sealed record TrayMenuItem(string Text, Action? OnClick, bool IsChecked = false, bool IsEnabled = true)
    {
        /// <summary>区切り線。</summary>
        public static TrayMenuItem Separator { get; } = new(string.Empty, null);

        public bool IsSeparator => Text.Length == 0;
    }

    /// <summary>
    /// Shell_NotifyIcon による最小構成のタスクトレイアイコン。
    /// 右クリックで「設定を開く / 終了」のコンテキストメニュー、ダブルクリックでメイン画面を開く。
    /// <see cref="CustomMenuItemsProvider"/> を設定したアプリは、その項目が既定の 2 項目の上に並ぶ。
    /// </summary>
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
        private const uint NIIF_WARNING = 0x00000002;
        private const uint MF_STRING = 0x00000000;
        private const uint MF_GRAYED = 0x00000001;
        private const uint MF_CHECKED = 0x00000008;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_BOTTOMALIGN = 0x0020;
        private const uint TPM_RETURNCMD = 0x0100;
        private const int ID_OPEN = 1001;
        private const int ID_EXIT = 1002;

        // アプリ追加項目のコマンド ID は ここ + 添字（既定項目の 1001/1002 と重ならない範囲）
        private const int ID_CUSTOM_BASE = 2001;
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        private readonly IntPtr _hwnd;
        private readonly uint _iconId;
        private IntPtr _iconHandle;
        private bool _isVisible;

        /// <summary>トレイアイコンをダブルクリック（メイン画面を開く）。</summary>
        public event Action? OpenMainWindowRequested;

        /// <summary>コンテキストメニューの「設定を開く」。</summary>
        public event Action? OpenSettingsRequested;

        /// <summary>コンテキストメニューの「終了」。AppRuntimeBase.ExitApplication のみを呼び出すこと。</summary>
        public event Action? ExitRequested;

        /// <summary>
        /// アプリ固有のメニュー項目を返す関数（右クリックのたびに呼ぶので、チェック状態は今の値で返せる）。
        /// null、または null / 空リストを返したときは既定の 2 項目だけを出す（既存アプリの見た目は変わらない）。
        /// </summary>
        public Func<IReadOnlyList<TrayMenuItem>?>? CustomMenuItemsProvider { get; set; }

        public TrayIconService(IntPtr hwnd, uint iconId = 1)
        {
            _hwnd = hwnd;
            _iconId = iconId;
            _iconHandle = LoadAppIcon();
        }

        public void Show()
        {
            if (_isVisible)
                return;
            AddIcon();
        }

        public void Hide()
        {
            if (!_isVisible)
                return;

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
        public void ShowBalloon(string title, string message) => ShowBalloon(title, message, isWarning: false);

        /// <summary>バルーン通知。isWarning が true なら警告アイコン（失敗の知らせなど）で出す。</summary>
        public void ShowBalloon(string title, string message, bool isWarning)
        {
            if (!_isVisible)
                return;

            var data = CreateNotifyData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(message, 255);
            data.dwInfoFlags = isWarning ? NIIF_WARNING : NIIF_INFO;
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

            // アプリ固有の項目を先に並べ、区切り線のあとに既定の「設定を開く / 終了」を置く
            IReadOnlyList<TrayMenuItem>? customItems = null;
            try
            {
                customItems = CustomMenuItemsProvider?.Invoke();
            }
            catch (Exception ex)
            {
                // 項目の組み立てに失敗しても、既定の 2 項目（特に「終了」）は必ず出す
                Debug.WriteLine($"Tray custom menu failed: {ex.Message}");
            }

            if (customItems is { Count: > 0 })
            {
                for (int i = 0; i < customItems.Count; i++)
                {
                    TrayMenuItem item = customItems[i];
                    if (item.IsSeparator)
                    {
                        AppendMenu(menu, MF_SEPARATOR, 0, null);
                        continue;
                    }

                    uint flags = MF_STRING
                        | (item.IsChecked ? MF_CHECKED : 0)
                        | (item.IsEnabled ? 0 : MF_GRAYED);
                    AppendMenu(menu, flags, ID_CUSTOM_BASE + i, item.Text);
                }

                AppendMenu(menu, MF_SEPARATOR, 0, null);
            }

            AppendMenu(menu, MF_STRING, ID_OPEN, AppHost.GetString("Tray_OpenSettings"));
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, ID_EXIT, AppHost.GetString("Tray_Exit"));

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
            else if (customItems != null
                     && cmd >= ID_CUSTOM_BASE
                     && cmd < ID_CUSTOM_BASE + customItems.Count)
            {
                // TrackPopupMenu は選ばれた ID を返すだけなので、添字に戻して該当項目の処理を呼ぶ
                customItems[(int)cmd - ID_CUSTOM_BASE].OnClick?.Invoke();
            }
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
                szTip = AppHost.GetString("AppName"),
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };
        }

        private static IntPtr LoadAppIcon()
        {
            // アイコンのファイル名はアプリごとに違うので AppProfile から取る
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", AppHost.Profile.TrayIconFileName);
            if (File.Exists(iconPath))
            {
                IntPtr fromFile = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (fromFile != IntPtr.Zero)
                    return fromFile;
            }

            // アイコン未設置時は exe 自体のアイコン（既定のものでも可）にフォールバックする
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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

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
