using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SmartPowerManager.Services;
using SmartPowerManager.Views;
using System.Diagnostics;
using System.Threading;
using Windows.Graphics;
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
    private const double PageHostVerticalPadding = 44;

    private readonly Settings _settings;
    private readonly ScheduleManager _scheduleManager;
    private readonly SyncCoordinatorService _syncCoordinator;
    private readonly AppState _appState;
    private readonly ScheduleExecutorService _executor;
    private readonly ConfirmationDialogService _confirmationDialog;
    private readonly TitleBarThemeHelper _titleBarThemeHelper;
    private readonly bool _launchInBackgroundMode;
    private bool _userWantsVisible;
    private TrayMessageWindow? _trayMessageWindow;
    private bool _canHideToTray;
    private bool _isExiting;
    private bool _uiInitialized;
    private bool _uiRenderedOnce;
    private bool _trayInitialized;
    private string _currentPageTag = "Shutdown";
    private CancellationTokenSource? _interactiveShowListenerCts;
#if DEBUG
    private Timer? _debuggerDetachTimer;
#endif

    public MainWindow(
        Settings settings,
        bool launchInBackground = false,
        bool requestVisibleOnLaunch = true,
        EventWaitHandle? interactiveShowEvent = null)
    {
        _launchInBackgroundMode = launchInBackground;
        _userWantsVisible = requestVisibleOnLaunch;
        _settings = settings;

        InitializeComponent();
        Title = Strings.Get("AppName");
        ApplyWindowIcon();

        _scheduleManager = new ScheduleManager();
        _syncCoordinator = new SyncCoordinatorService();
        _appState = new AppState(_settings, _scheduleManager, _syncCoordinator);
        _appState.RequestSharedScheduleRefresh = () =>
            DispatcherQueue.TryEnqueue(RefreshCurrentPage);

        ThemeService.AttachRoot(RootGrid);
        _titleBarThemeHelper = new TitleBarThemeHelper(this, RootGrid);

        _confirmationDialog = new ConfirmationDialogService(DispatcherQueue);

        _executor = new ScheduleExecutorService(
            _scheduleManager,
            _syncCoordinator,
            _confirmationDialog,
            DispatcherQueue,
            _settings);
        _appState.Executor = _executor;

        WireExecutorEvents();

        StartupManager.ValidateAutoStart(_settings.AutoStart);

        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;
        RootGrid.Loaded += RootGrid_Loaded;
        ContentAreaGrid.SizeChanged += (_, __) => UpdatePageHostWidth();
        ContentFrame.NavigationFailed += ContentFrame_NavigationFailed;
        Activated += MainWindow_Activated;

        StartInteractiveShowListener(interactiveShowEvent);
#if DEBUG
        if (Debugger.IsAttached)
            StartDebuggerDetachWatch();
#endif
    }

    private void WireExecutorEvents()
    {
        _executor.LogAdded += msg => DispatcherQueue.TryEnqueue(() => _appState.AddActivityLog(msg));
        _executor.SchedulesChanged += () => DispatcherQueue.TryEnqueue(RefreshCurrentPage);
        _executor.MonitoringStateChanged += () => DispatcherQueue.TryEnqueue(RefreshCurrentPage);
        _executor.ShowWindowRequested += () => DispatcherQueue.TryEnqueue(RequestInteractiveShow);
        _executor.PendingConfirmationRequested += () =>
            DispatcherQueue.TryEnqueue(async () => await _executor.HandlePendingActionAsync());
        _syncCoordinator.LogAdded += msg => DispatcherQueue.TryEnqueue(() => _appState.AddStartupLog(msg));
    }

    private void ApplyWindowIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.ico");
        if (!File.Exists(iconPath))
            return;

        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        IntPtr hIcon = PInvokeHelper.LoadImageIcon(iconPath);
        if (hIcon != IntPtr.Zero)
            PInvokeHelper.SendMessageIcon(hwnd, hIcon);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isExiting)
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
        double availableHeight = ContentAreaGrid.ActualHeight - PageHostVerticalPadding;

        if (availableWidth > 0)
        {
            double minWidth = PageHostGrid.MinWidth > 0 ? PageHostGrid.MinWidth : PageHostMinWidth;
            PageHostGrid.Width = Math.Clamp(availableWidth, minWidth, PageHostMaxWidth);
        }

        if (availableHeight > 0)
        {
            PageHostGrid.Height = availableHeight;
            PageHostGrid.MaxHeight = availableHeight;
        }
    }

    private void SaveWindowBounds()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            // 最大化中は復元用の通常サイズ・位置を上書きしない
            _settings.WindowMaximized = true;
            _settings.Save();
            return;
        }

        if (presenter.State != OverlappedPresenterState.Restored)
            return;

        var size = AppWindow.Size;
        if (size.Width < 400 || size.Height < 300)
            return;

        // 最大化遷移中に「作業領域ほぼいっぱい」のサイズが来ても復元サイズを壊さない
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display != null)
        {
            var work = display.WorkArea;
            if (size.Width >= work.Width - 8 && size.Height >= work.Height - 8)
            {
                _settings.WindowMaximized = true;
                _settings.Save();
                return;
            }
        }

        var pos = AppWindow.Position;
        _settings.WindowMaximized = false;
        _settings.WindowWidth = size.Width;
        _settings.WindowHeight = size.Height;
        _settings.WindowX = pos.X;
        _settings.WindowY = pos.Y;
        _settings.Save();
    }

    private void RestoreWindowBounds()
    {
        int width = _settings.WindowWidth > 0 ? _settings.WindowWidth : DefaultClientWidth;
        int height = _settings.WindowHeight > 0 ? _settings.WindowHeight : DefaultClientHeight;

        DisplayArea? display = null;
        if (_settings.WindowX >= 0 && _settings.WindowY >= 0)
            display = DisplayArea.GetFromPoint(new PointInt32(_settings.WindowX, _settings.WindowY), DisplayAreaFallback.Nearest);
        display ??= DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

        if (display != null)
        {
            var work = display.WorkArea;

            // 以前最大化サイズが保存されていた場合はデフォルトに戻す
            if (width >= work.Width - 8 || height >= work.Height - 8)
            {
                width = DefaultClientWidth;
                height = DefaultClientHeight;
            }

            width = Math.Clamp(width, 400, work.Width);
            height = Math.Clamp(height, 300, work.Height);

            if (_settings.WindowMaximized)
            {
                // 作業領域内に通常サイズを置いてから Maximize（タスクバー重なり防止）
                AppWindow.Resize(new SizeInt32(width, height));
                int x = work.X + Math.Max(0, (work.Width - width) / 2);
                int y = work.Y + Math.Max(0, (work.Height - height) / 2);
                AppWindow.Move(new PointInt32(x, y));
                if (AppWindow.Presenter is OverlappedPresenter maximizedPresenter)
                    maximizedPresenter.Maximize();
                return;
            }

            AppWindow.Resize(new SizeInt32(width, height));
            if (_settings.WindowX >= 0 && _settings.WindowY >= 0)
            {
                int maxX = work.X + Math.Max(0, work.Width - width);
                int maxY = work.Y + Math.Max(0, work.Height - height);
                int x = Math.Clamp(_settings.WindowX, work.X, maxX);
                int y = Math.Clamp(_settings.WindowY, work.Y, maxY);
                AppWindow.Move(new PointInt32(x, y));
            }

            return;
        }

        AppWindow.Resize(new SizeInt32(width, height));
        if (_settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter fallbackPresenter)
            fallbackPresenter.Maximize();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            _titleBarThemeHelper?.ScheduleUpdate();

        if (_userWantsVisible || !_launchInBackgroundMode)
            return;

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Hide();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_uiInitialized)
            return;

        _uiInitialized = true;
        RootGrid.Loaded -= RootGrid_Loaded;

        RestoreWindowBounds();
        ConfigureMinimumWindowSize();
        UpdatePageHostWidth();

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isExiting)
                return;

            NavigateToPage("Shutdown", force: true);
            _executor.Initialize();

            if (_userWantsVisible)
            {
                ShowMainWindow();
                CompositionTarget.Rendering += OnFirstFrameRendered;
            }
            else
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, InitializeBackgroundServices);
            }

            if (!_scheduleManager.Data.DisclaimerAccepted)
                _ = ShowDisclaimerDialogAsync();
        });
    }

    private async Task ShowDisclaimerDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = Strings.Get("Disclaimer_Title"),
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = Strings.Get("Disclaimer_Body"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                }
            },
            PrimaryButtonText = Strings.Get("Disclaimer_Accept"),
            CloseButtonText = Strings.Get("Disclaimer_Exit"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _scheduleManager.Data.DisclaimerAccepted = true;
            _scheduleManager.Save();
        }
        else
        {
            _isExiting = true;
            Close();
        }
    }

    private void OnFirstFrameRendered(object? sender, object e)
    {
        if (_uiRenderedOnce)
            return;

        _uiRenderedOnce = true;
        CompositionTarget.Rendering -= OnFirstFrameRendered;
        InitializeTrayIfNeeded();
        ApplyBackgroundVisibilityPolicy();
    }

    private void InitializeBackgroundServices()
    {
        if (_isExiting || _uiRenderedOnce)
            return;

        _uiRenderedOnce = true;
        InitializeTrayIfNeeded();
        ApplyBackgroundVisibilityPolicy();
    }

    private void ApplyBackgroundVisibilityPolicy()
    {
        if (_userWantsVisible || !_launchInBackgroundMode)
            return;

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Hide();
        EnsureTrayIconVisible();
    }

    public void RequestInteractiveShow()
    {
        _userWantsVisible = true;
        if (!_uiInitialized)
            return;

        ShowMainWindow(bringToForeground: true);
        if (_uiRenderedOnce)
            InitializeTrayIfNeeded();
    }

    private void InitializeTrayIfNeeded()
    {
        if (_trayInitialized)
            return;

#if DEBUG
        // デバッグ実行中はトレイ常駐を無効化し、ウィンドウ閉鎖で完全終了させる
        if (Debugger.IsAttached)
            return;
#endif

        _trayInitialized = true;
        SetupTrayIcon();
        EnsureTrayIconVisible();
    }

    private void SetupTrayIcon()
    {
        _trayMessageWindow = new TrayMessageWindow();
        _trayMessageWindow.TrayIcon.OpenMainWindowRequested += () => DispatcherQueue.TryEnqueue(RequestInteractiveShow);
        _trayMessageWindow.TrayIcon.OpenSettingsRequested += () => DispatcherQueue.TryEnqueue(() =>
        {
            RequestInteractiveShow();
            NavigateToPage("Settings");
        });
        _trayMessageWindow.TrayIcon.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);
        _canHideToTray = true;
    }

    private void EnsureTrayIconVisible()
    {
        if (!_canHideToTray)
            return;

        _trayMessageWindow?.TrayIcon.Show();
    }

    private void StartInteractiveShowListener(EventWaitHandle? interactiveShowEvent)
    {
        if (interactiveShowEvent == null)
            return;

        _interactiveShowListenerCts = new CancellationTokenSource();
        var token = _interactiveShowListenerCts.Token;

        Task.Run(() =>
        {
            while (!token.IsCancellationRequested && !_isExiting)
            {
                try
                {
                    if (!interactiveShowEvent.WaitOne(500))
                        continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (token.IsCancellationRequested || _isExiting)
                    break;

                DispatcherQueue.TryEnqueue(RequestInteractiveShow);
            }
        }, token);
    }

    private void ShowMainWindow(bool bringToForeground = false)
    {
        AppWindow.IsShownInSwitchers = true;
        AppWindow.Show();
        if (bringToForeground)
            Activate();
    }

#if DEBUG
    private void StartDebuggerDetachWatch()
    {
        _debuggerDetachTimer = new Timer(_ =>
        {
            if (Debugger.IsAttached || _isExiting)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isExiting)
                    ExitApplication();
            });
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
#endif

    private void ExitApplication()
    {
        _isExiting = true;
#if DEBUG
        _debuggerDetachTimer?.Dispose();
        _debuggerDetachTimer = null;
#endif
        SaveWindowBounds();
        _executor.Dispose();
        _trayMessageWindow?.Dispose();
        SingleInstanceManager.Release();
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting)
            return;

        if (_canHideToTray)
        {
            args.Cancel = true;
            SaveWindowBounds();
            AppWindow.Hide();
            EnsureTrayIconVisible();
            return;
        }

        _isExiting = true;
        SaveWindowBounds();
        _executor.Dispose();
        _trayMessageWindow?.Dispose();
        SingleInstanceManager.Release();
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
        if (args.IsSettingsInvoked)
        {
            NavigateToPage("Settings");
            return;
        }

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
            "Update" => typeof(UpdatePage),
            "Info" => typeof(InfoPage),
            _ => typeof(ShutdownPage)
        };

        if (force || ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, _appState);

        UpdateNavSelection(tag);
    }

    private void UpdateNavSelection(string tag)
    {
        foreach (NavigationViewItem item in NavView.MenuItems.OfType<NavigationViewItem>())
            item.IsSelected = item.Tag as string == tag;

        foreach (NavigationViewItem item in NavView.FooterMenuItems.OfType<NavigationViewItem>())
            item.IsSelected = item.Tag as string == tag;

        NavView.IsPaneOpen = true;
    }

    private void RefreshCurrentPage()
    {
        if (ContentFrame.Content is ISchedulePage schedulePage)
            schedulePage.RefreshDisplay();
    }
}
