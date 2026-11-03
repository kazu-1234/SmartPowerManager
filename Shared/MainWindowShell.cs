using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace WinUiShared;

/// <summary>
/// MainWindow の定型処理（テーマ・タイトルバー・最小サイズ・窓の位置の保存と復元・左ナビのページ移動・閉じる処理）を受け持つ。
/// 以前は全アプリの MainWindow.xaml.cs に同じコードがコピーされていた。
///
/// 使い方（各アプリの MainWindow）:
///   XAML（RootGrid / TitleBar / NavigationView / Frame）はアプリ側に置いたまま、コンストラクタで
///   <c>_shell = new MainWindowShell(this, new MainWindowShellOptions { ... });</c> を作り、
///   IAppMainWindow の 3 つのメソッドを _shell に渡す。ナビの ItemInvoked は <see cref="OnNavItemInvoked"/> へ。
/// アプリ固有の画面（再生バー・ダイアログなど）はアプリの MainWindow に残す。
/// </summary>
public sealed class MainWindowShell
{
    private readonly Window _window;
    private readonly MainWindowShellOptions _options;
    private readonly TitleBarThemeHelper? _titleBarThemeHelper;
    private bool _windowBoundsReady;
    private string _currentPageTag;

    public MainWindowShell(Window window, MainWindowShellOptions options)
    {
        _window = window;
        _options = options;
        _currentPageTag = options.DefaultPageTag;

        string appName = AppHost.GetString("AppName");
        window.Title = appName;
        if (options.TitleBar != null)
        {
            options.TitleBar.Title = appName;
            _titleBarThemeHelper = new TitleBarThemeHelper(window, options.RootElement, options.TitleBar);
        }

        ApplyWindowIcon();
        ThemeService.AttachRoot(options.RootElement);

        window.AppWindow.Closing += AppWindow_Closing;
        window.AppWindow.Changed += AppWindow_Changed;
        options.ContentFrame.NavigationFailed += ContentFrame_NavigationFailed;

        ConfigureMinimumWindowSize();
    }

    /// <summary>今表示しているページのタグ。</summary>
    public string CurrentPageTag => _currentPageTag;

    /// <summary>位置・サイズの保存を始めてよいか（復元が済んでから true）。</summary>
    public bool WindowBoundsReady => _windowBoundsReady;

    /// <summary>
    /// Auto Dark Mode と同じ順序: Navigate → 位置サイズ復元 → Activate（ちらつき防止のため順序固定）。
    /// </summary>
    public void PrepareAndActivate(string? initialPageTag = null)
    {
        string tag = string.IsNullOrEmpty(initialPageTag) ? _options.DefaultPageTag : initialPageTag;
        NavigateToPage(tag, force: true, suppressTransition: true);
        WindowPlacementHelper.Restore(_window, _options.PlacementSettings, _options.DefaultWidth, _options.DefaultHeight);
        _windowBoundsReady = true;

        _window.AppWindow.IsShownInSwitchers = true;
        _window.Activate();
    }

    /// <summary>既に開いている窓で指定ページへ移動する（トレイの「設定を開く」など）。</summary>
    public void NavigateToPageTag(string tag) => NavigateToPage(tag, force: false, suppressTransition: true);

    /// <summary>× 押下直後など、今の位置・サイズを保存する（復元前は何もしない）。</summary>
    public void SaveWindowBounds()
    {
        if (_windowBoundsReady)
            WindowPlacementHelper.Save(_window, _options.PlacementSettings);
    }

    /// <summary>閉じる処理の途中などで、以後の位置保存を止める（LeftHandDevice の閉じる処理）。</summary>
    public void StopSavingWindowBounds() => _windowBoundsReady = false;

    /// <summary>左ナビの項目が押されたとき（XAML の ItemInvoked から呼ぶ）。</summary>
    public void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
            NavigateToPage(tag);
    }

    /// <summary>
    /// タグのページへ移動する。force = 同じページでも作り直す。suppressTransition = 切り替えのアニメーションを出さない。
    /// </summary>
    public void NavigateToPage(string tag, bool force = false, bool suppressTransition = false)
    {
        Frame frame = _options.ContentFrame;
        if (!force && _currentPageTag == tag && frame.CurrentSourcePageType != null)
        {
            UpdateNavSelection(tag);
            return;
        }

        _currentPageTag = tag;
        Type pageType = _options.ResolvePageType(tag);

        if (force || frame.CurrentSourcePageType != pageType)
        {
            object? parameter = _options.NavigationParameter?.Invoke();
            NavigationTransitionInfo? transition = suppressTransition
                ? new SuppressNavigationTransitionInfo()
                : _options.NavigationTransition;
            if (transition != null)
                frame.Navigate(pageType, parameter, transition);
            else
                frame.Navigate(pageType, parameter);
        }

        UpdateNavSelection(tag);
    }

    /// <summary>左ナビの選択を、表示中のページに合わせる（見つからなければ既定ページの項目）。</summary>
    public void UpdateNavSelection(string tag)
    {
        NavigationView nav = _options.NavView;
        NavigationViewItem? match = FindNavItem(nav, tag) ?? FindNavItem(nav, _options.DefaultPageTag);
        if (match != null)
            nav.SelectedItem = match;
    }

    private static NavigationViewItem? FindNavItem(NavigationView nav, string tag)
    {
        NavigationViewItem? match = null;
        foreach (var item in nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
                match = item;
        }

        foreach (var item in nav.FooterMenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
                match = item;
        }

        return match;
    }

    private void ApplyWindowIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", _options.IconFileName);
        if (!File.Exists(iconPath))
            return;

        try
        {
            _window.AppWindow.SetIcon(iconPath);
        }
        catch
        {
            // SetIcon 失敗時はタイトルバー / トレイのフォールバック表示に任せる
        }
    }

    private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        // アプリ名は文言から取る（以前 BlueShift だけ固定の文字列だった）
        _window.Title = $"{AppHost.GetString("AppName")} - {e.Exception?.Message}";
    }

    private void ConfigureMinimumWindowSize()
    {
        if (_window.AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        presenter.IsResizable = true;
        double scaleFactor = _options.RootElement.XamlRoot?.RasterizationScale ?? 1.0;
        presenter.PreferredMinimumWidth = (int)(_options.MinimumWidth * scaleFactor);
        presenter.PreferredMinimumHeight = (int)(_options.MinimumHeight * scaleFactor);
        presenter.PreferredMaximumWidth = 10000;
        presenter.PreferredMaximumHeight = 10000;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (IsExiting() || !_windowBoundsReady)
            return;

        if (args.DidSizeChange || args.DidPositionChange)
            SaveWindowBounds();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (IsExiting())
            return;

        // 閉じてよいか（処理中の確認ダイアログを出すアプリなど）。false なら閉じない
        if (_options.CanClose?.Invoke() == false)
        {
            args.Cancel = true;
            return;
        }

        SaveWindowBounds();
        _options.OnBeforeClose?.Invoke();
        AppHost.Runtime?.OnMainWindowClosing(_window);

        // × は本当に Close する（Hide しない）。閉じた後どうするか（常駐を続ける / プロセス終了 /
        // デバッグ実行中の終了）は AppRuntimeBase が AppProfile の型に従って決める
        args.Cancel = false;
        _window.AppWindow.Closing -= AppWindow_Closing;
        _window.AppWindow.Changed -= AppWindow_Changed;
    }

    private static bool IsExiting() => AppHost.Runtime?.IsExitingProcess == true;
}

/// <summary><see cref="MainWindowShell"/> に渡す、アプリごとに違う値。</summary>
public sealed class MainWindowShellOptions
{
    /// <summary>テーマを当てる一番外側の要素（MainWindow.xaml の RootGrid）。</summary>
    public required FrameworkElement RootElement { get; init; }

    /// <summary>WinUI の TitleBar（無いアプリは null。タイトルバーの色合わせを行わない）。</summary>
    public TitleBar? TitleBar { get; init; }

    public required NavigationView NavView { get; init; }

    public required Frame ContentFrame { get; init; }

    /// <summary>窓の位置・サイズの保存先（各アプリの Settings）。</summary>
    public required IWindowPlacementSettings PlacementSettings { get; init; }

    /// <summary>保存値が無いときの窓の大きさ（物理ピクセル）。</summary>
    public int DefaultWidth { get; init; } = 960;

    public int DefaultHeight { get; init; } = 680;

    /// <summary>窓の最小サイズ（DIP。表示倍率を掛けて使う）。</summary>
    public double MinimumWidth { get; init; } = 870;

    public double MinimumHeight { get; init; } = 600;

    /// <summary>最初に開くページのタグ。</summary>
    public string DefaultPageTag { get; init; } = "Home";

    /// <summary>タグからページの型を返す（知らないタグは既定ページの型を返す）。</summary>
    public required Func<string, Type> ResolvePageType { get; init; }

    /// <summary>ページへ渡す引数（AppState など。null なら引数なし）。</summary>
    public Func<object?>? NavigationParameter { get; init; }

    /// <summary>ナビで切り替えたときのアニメーション（null なら Frame の既定）。</summary>
    public NavigationTransitionInfo? NavigationTransition { get; init; }

    /// <summary>Assets 内の窓アイコンのファイル名。</summary>
    public string IconFileName { get; init; } = "AppIcon.ico";

    /// <summary>× を押したとき、閉じてよいか（false なら閉じない）。</summary>
    public Func<bool>? CanClose { get; init; }

    /// <summary>閉じる直前（位置保存のあと）にアプリ固有の後片付けをする。</summary>
    public Action? OnBeforeClose { get; init; }
}
