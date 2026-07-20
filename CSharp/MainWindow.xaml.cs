using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using SmartPowerManager.Views;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WinRT.Interop;

namespace SmartPowerManager;

public sealed partial class MainWindow : Window
{
    private const int DefaultClientWidth = 960;
    private const int DefaultClientHeight = 680;
    private const double MinimumWindowWidth = 870;
    private const double MinimumWindowHeight = 600;
    private const double PageHostMinWidth = 560;
    private const double PageHostMaxWidth = 1400;
    private const double PageHostHorizontalPadding = 48;

    private readonly AppRuntime _runtime;
    private readonly AppState _appState;
    private bool _windowBoundsReady;
    private string _currentPageTag = "Shutdown";
    private readonly TitleBarThemeHelper _titleBarThemeHelper;

    public MainWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _appState = runtime.AppState;

        InitializeComponent();
        Title = Strings.Get("AppName");
        AppTitleBar.Title = Strings.Get("AppName");
        ApplyWindowIcon();

        ThemeService.AttachRoot(RootGrid);
        _titleBarThemeHelper = new TitleBarThemeHelper(this, RootGrid, AppTitleBar);

        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;
        ContentAreaGrid.SizeChanged += (_, __) => UpdatePageHostWidth();
        ContentFrame.NavigationFailed += ContentFrame_NavigationFailed;

        ConfigureMinimumWindowSize();
    }

    /// <summary>
    /// Auto Dark Mode と同じ順序: Navigate → 位置サイズ復元 → Activate。
    /// </summary>
    public void PrepareAndActivate(string? initialPageTag = null)
    {
        string tag = string.IsNullOrEmpty(initialPageTag) ? GetDefaultPageTag() : initialPageTag;
        NavigateToPage(tag, force: true);
        RestoreWindowBounds();
        UpdatePageHostWidth();
        _windowBoundsReady = true;

        AppWindow.IsShownInSwitchers = true;
        Activate();
    }

    public void NavigateToPageTag(string tag) => NavigateToPage(tag, force: false);

    internal void SaveWindowBoundsFromRuntime()
    {
        SaveWindowBounds();
    }

    public void RefreshCurrentPage()
    {
        if (ContentFrame.Content is ISchedulePage schedulePage)
            schedulePage.RefreshDisplay();
    }

    private static string GetDefaultPageTag() => "Shutdown";

    private void ApplyWindowIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.ico");
        if (!File.Exists(iconPath))
            return;

        try
        {
            AppWindow.SetIcon(iconPath);
        }
        catch
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            IntPtr hIcon = PInvokeHelper.LoadImageIcon(iconPath);
            if (hIcon != IntPtr.Zero)
                PInvokeHelper.SendMessageIcon(hwnd, hIcon);
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_runtime.IsExitingProcess || !_windowBoundsReady)
            return;

        if (args.DidSizeChange || args.DidPositionChange)
            SaveWindowBounds();

        if (args.DidSizeChange)
            UpdatePageHostWidth();
    }

    private void UpdatePageHostWidth()
    {
        if (ContentAreaGrid == null || PageHostGrid == null)
            return;

        double availableWidth = ContentAreaGrid.ActualWidth - PageHostHorizontalPadding;
        if (availableWidth > 0)
        {
            double minWidth = PageHostGrid.MinWidth > 0 ? PageHostGrid.MinWidth : PageHostMinWidth;
            double nextWidth = Math.Clamp(availableWidth, minWidth, PageHostMaxWidth);
            if (Math.Abs(PageHostGrid.Width - nextWidth) > 0.5)
                PageHostGrid.Width = nextWidth;
        }

        PageHostGrid.ClearValue(FrameworkElement.HeightProperty);
        PageHostGrid.ClearValue(FrameworkElement.MaxHeightProperty);
        PageHostGrid.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void SaveWindowBounds()
    {
        if (!_windowBoundsReady)
            return;

        WindowPlacementHelper.Save(this, _runtime.Settings);
    }

    private void RestoreWindowBounds()
    {
        WindowPlacementHelper.Restore(this, _runtime.Settings, DefaultClientWidth, DefaultClientHeight);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_runtime.IsExitingProcess)
            return;

        SaveWindowBounds();
        _runtime.OnMainWindowClosing(this);

#if DEBUG
        if (Debugger.IsAttached)
        {
            args.Cancel = false;
            Closed += MainWindow_ClosedDebugExit;
            AppWindow.Closing -= AppWindow_Closing;
            AppWindow.Changed -= AppWindow_Changed;
            return;
        }
#endif

        args.Cancel = false;
        AppWindow.Closing -= AppWindow_Closing;
        AppWindow.Changed -= AppWindow_Changed;
    }

    private void MainWindow_ClosedDebugExit(object sender, WindowEventArgs e)
    {
        Closed -= MainWindow_ClosedDebugExit;
        if (!_runtime.IsExitingProcess)
            _runtime.ExitApplication();
    }

    private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        Title = $"{Strings.Get("AppName")} - {e.Exception?.Message}";
    }

    private void ConfigureMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        presenter.IsResizable = true;
        double scaleFactor = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        presenter.PreferredMinimumWidth = (int)(MinimumWindowWidth * scaleFactor);
        presenter.PreferredMinimumHeight = (int)(MinimumWindowHeight * scaleFactor);
        presenter.PreferredMaximumWidth = 10000;
        presenter.PreferredMaximumHeight = 10000;
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
            NavigateToPage(tag);
    }

    private void NavigateToPage(string tag, bool force = false)
    {
        if (!force && _currentPageTag == tag && ContentFrame.CurrentSourcePageType != null)
        {
            UpdateNavSelection(tag);
            return;
        }

        _currentPageTag = tag;
        Type pageType = tag switch
        {
            "Restart" => typeof(RestartPage),
            "Wake" => typeof(WakePage),
            "Settings" => typeof(SettingsPage),
            "Info" => typeof(InfoPage),
            _ => typeof(ShutdownPage)
        };

        // 既定の横スライド遷移はページ切替のたびに左右位置がずれて見えるため常に抑制する
        if (force || ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, _appState, new SuppressNavigationTransitionInfo());

        UpdateNavSelection(tag);
    }

    private void UpdateNavSelection(string tag)
    {
        NavigationViewItem? match = null;
        foreach (NavigationViewItem item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
                match = item;
        }

        foreach (NavigationViewItem item in NavView.FooterMenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
                match = item;
        }

        if (match != null)
            NavView.SelectedItem = match;
        else if (NavItemHome != null)
            NavView.SelectedItem = NavItemHome;
    }
}
