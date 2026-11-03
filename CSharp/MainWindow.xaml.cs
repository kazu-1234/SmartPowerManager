using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartPowerManager.Views;
using System;
using System.IO;
using WinRT.Interop;
using WinUiShared;

namespace SmartPowerManager;

/// <summary>
/// メインウィンドウ。テーマ・タイトルバー・窓の位置の保存と復元・左ナビのページ移動・閉じる処理は
/// 共通部の MainWindowShell が行う。このファイルには、このアプリ固有の値（サイズ・ページ表）と、
/// アイコンの設定（失敗時の予備あり）・スケジュール表示の更新だけを書く。
/// </summary>
public sealed partial class MainWindow : Window, IAppMainWindow
{
    // 保存値が無いときの窓の大きさ（物理ピクセル）と最小サイズ（DIP）
    private const int DefaultClientWidth = 960;
    private const int DefaultClientHeight = 680;
    private const double MinimumWindowWidth = 870;
    private const double MinimumWindowHeight = 600;

    private const string IconFileName = "app_icon.ico";

    private readonly MainWindowShell _shell;

    public MainWindow(AppRuntime runtime)
    {
        InitializeComponent();

        _shell = new MainWindowShell(this, new MainWindowShellOptions
        {
            RootElement = RootGrid,
            TitleBar = AppTitleBar,
            NavView = NavView,
            ContentFrame = ContentFrame,
            PlacementSettings = runtime.Settings,
            DefaultWidth = DefaultClientWidth,
            DefaultHeight = DefaultClientHeight,
            MinimumWidth = MinimumWindowWidth,
            MinimumHeight = MinimumWindowHeight,
            DefaultPageTag = "Shutdown",
            // アイコンは下の ApplyWindowIcon が設定する（SetIcon が失敗したときに LoadImage で付け直す予備があるため）。
            // 共通部にも設定させると二重になるので、空にして共通部の設定を止める
            IconFileName = string.Empty,
            ResolvePageType = ResolvePageType,
            NavigationParameter = () => runtime.AppState
        });

        ApplyWindowIcon();
    }

    public void PrepareAndActivate(string? initialPageTag = null) => _shell.PrepareAndActivate(initialPageTag);

    public void NavigateToPageTag(string tag) => _shell.NavigateToPageTag(tag);

    public void SaveWindowBoundsFromRuntime() => _shell.SaveWindowBounds();

    /// <summary>表示中のスケジュールのページを描き直す（スケジュールの実行・変更の後に AppRuntime から呼ぶ）。</summary>
    public void RefreshCurrentPage()
    {
        if (ContentFrame.Content is ISchedulePage schedulePage)
            schedulePage.RefreshDisplay();
    }

    /// <summary>左ナビのタグ → ページの型（知らないタグはシャットダウン）。ページを足したらここと XAML の項目を足す。</summary>
    private static Type ResolvePageType(string tag) => tag switch
    {
        "Restart" => typeof(RestartPage),
        "Wake" => typeof(WakePage),
        "Settings" => typeof(SettingsPage),
        "Info" => typeof(InfoPage),
        _ => typeof(ShutdownPage)
    };

    private void ApplyWindowIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", IconFileName);
        if (!File.Exists(iconPath))
            return;

        try
        {
            AppWindow.SetIcon(iconPath);
        }
        catch
        {
            // SetIcon が失敗する環境向けの予備: Win32 の LoadImage で読み、WM_SETICON で窓に付ける
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            IntPtr hIcon = PInvokeHelper.LoadImageIcon(iconPath);
            if (hIcon != IntPtr.Zero)
                PInvokeHelper.SendMessageIcon(hwnd, hIcon);
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args) =>
        _shell.OnNavItemInvoked(sender, args);
}
