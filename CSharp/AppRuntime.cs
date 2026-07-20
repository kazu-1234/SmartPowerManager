using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SmartPowerManager.Services;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;

namespace SmartPowerManager
{
    /// <summary>
    /// プロセス寿命: トレイ・スケジュール executor・二重起動イベント。MainWindow は都度生成（ADM 同等）。
    /// </summary>
    public sealed class AppRuntime : IDisposable
    {
        private readonly Application _app;
        private readonly Settings _settings;
        private readonly ScheduleManager _scheduleManager;
        private readonly SyncCoordinatorService _syncCoordinator;
        private readonly AppState _appState;
        private readonly ScheduleExecutorService _executor;
        private readonly ConfirmationDialogService _confirmationDialog;

        private MainWindow? _mainWindow;
        private TrayMessageWindow? _trayMessageWindow;
        private CancellationTokenSource? _listenerCts;
        private bool _trayInitialized;
        private bool _executorInitialized;
        private bool _isExitingProcess;
#if DEBUG
        private Timer? _debuggerDetachTimer;
#endif

        public AppRuntime(Application app, Settings settings)
        {
            _app = app;
            _settings = settings;
            _scheduleManager = new ScheduleManager();
            _syncCoordinator = new SyncCoordinatorService();
            _appState = new AppState(_settings, _scheduleManager, _syncCoordinator);

            var dq = DispatcherQueue.GetForCurrentThread();
            _confirmationDialog = new ConfirmationDialogService(dq);
            _executor = new ScheduleExecutorService(
                _scheduleManager,
                _syncCoordinator,
                _confirmationDialog,
                dq,
                _settings);
            _appState.Executor = _executor;
            _appState.ApplyTrayIconVisibility = ApplyTrayIconVisibility;
            _appState.RequestSharedScheduleRefresh = RequestSharedScheduleRefresh;

            WireExecutorEvents();
        }

        public AppState AppState => _appState;
        public Settings Settings => _settings;
        public bool IsExitingProcess => _isExitingProcess;

        public void Start(bool launchInBackground, bool requestInteractiveShow)
        {
            ThemeService.Initialize(_settings.ThemePreference);
            StartListeners();
            EnsureExecutor();

            if (!ShouldUseTray())
            {
#if DEBUG
                if (Debugger.IsAttached)
                    StartDebuggerDetachWatch();
#endif
                if (requestInteractiveShow || !launchInBackground)
                    ShowOrCreateMainWindow();
                return;
            }

            EnsureTray();

            if (requestInteractiveShow || !launchInBackground)
                ShowOrCreateMainWindow();
        }

        public void ShowOrCreateMainWindow(string? pageTag = null)
        {
            if (_isExitingProcess)
                return;

            GetDispatcherQueue()?.TryEnqueue(() => ShowOrCreateMainWindowCore(pageTag));
        }

        private void ShowOrCreateMainWindowCore(string? pageTag = null)
        {
            if (_isExitingProcess)
                return;

            if (_mainWindow != null)
            {
                BringWindowToForeground(_mainWindow);
                if (pageTag != null)
                    _mainWindow.NavigateToPageTag(pageTag);
                return;
            }

            _mainWindow = new MainWindow(this);
            _mainWindow.Closed += MainWindow_Closed;
            _mainWindow.PrepareAndActivate(pageTag);
        }

        public void OnMainWindowClosing(MainWindow window)
        {
            if (_isExitingProcess || window != _mainWindow)
                return;

            window.SaveWindowBoundsFromRuntime();
        }

        public void RequestSharedScheduleRefresh()
        {
            GetDispatcherQueue()?.TryEnqueue(() => _mainWindow?.RefreshCurrentPage());
        }

        public void ExitApplication()
        {
            if (_isExitingProcess)
                return;

            _isExitingProcess = true;
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _listenerCts = null;
#if DEBUG
            _debuggerDetachTimer?.Dispose();
            _debuggerDetachTimer = null;
#endif

            _executor.Dispose();
            _trayMessageWindow?.Dispose();
            _trayMessageWindow = null;

            if (!_settings.AutoStart)
                StartupManager.SyncAutostartWithSettings(false);

            SingleInstanceManager.Release();

            if (_mainWindow != null)
            {
                try { _mainWindow.Close(); } catch { }
                _mainWindow = null;
            }

            _app.Exit();
        }

        public void ApplyTrayIconVisibility()
        {
            if (_trayMessageWindow == null)
                return;

            if (_settings.HideTrayIcon)
                _trayMessageWindow.TrayIcon.Hide();
            else
                _trayMessageWindow.TrayIcon.Show();
        }

        public void Dispose() => ExitApplication();

        private void MainWindow_Closed(object sender, WindowEventArgs e)
        {
            if (ReferenceEquals(_mainWindow, sender))
                _mainWindow = null;
        }

        private void WireExecutorEvents()
        {
            _executor.LogAdded += msg =>
                GetDispatcherQueue()?.TryEnqueue(() => _appState.AddActivityLog(msg));
            _executor.SchedulesChanged += () =>
                GetDispatcherQueue()?.TryEnqueue(RequestSharedScheduleRefresh);
            _executor.MonitoringStateChanged += () =>
                GetDispatcherQueue()?.TryEnqueue(RequestSharedScheduleRefresh);
            _executor.ShowWindowRequested += () =>
                GetDispatcherQueue()?.TryEnqueue(() => ShowOrCreateMainWindow());
            _executor.PendingConfirmationRequested += () =>
                GetDispatcherQueue()?.TryEnqueue(HandlePendingConfirmationAsync);
            _syncCoordinator.LogAdded += msg =>
                GetDispatcherQueue()?.TryEnqueue(() => _appState.AddStartupLog(msg));
        }

        private void HandlePendingConfirmationAsync()
        {
            GetDispatcherQueue()?.TryEnqueue(async () =>
            {
                ShowOrCreateMainWindowCore();
                await _executor.HandlePendingActionAsync();
            });
        }

        private static bool ShouldUseTray()
        {
#if DEBUG
            if (Debugger.IsAttached)
                return false;
#endif
            return true;
        }

        private void EnsureExecutor()
        {
            if (_executorInitialized)
                return;

            _executorInitialized = true;
            _executor.Initialize();
        }

        private void EnsureTray()
        {
            if (_trayInitialized)
                return;

            _trayInitialized = true;
            try
            {
                _trayMessageWindow = new TrayMessageWindow();
                _trayMessageWindow.TrayIcon.OpenMainWindowRequested += () => ShowOrCreateMainWindow();
                _trayMessageWindow.TrayIcon.OpenSettingsRequested += () => ShowOrCreateMainWindow("Settings");
                _trayMessageWindow.TrayIcon.ExitRequested += () => GetDispatcherQueue()?.TryEnqueue(ExitApplication);
                ApplyTrayIconVisibility();
            }
            catch
            {
                _trayMessageWindow?.Dispose();
                _trayMessageWindow = null;
                _trayInitialized = false;
            }
        }

        private void StartListeners()
        {
            var showEvent = SingleInstanceManager.InteractiveShowEvent;
            var exitEvent = SingleInstanceManager.ExitEvent;
            if (showEvent == null && exitEvent == null)
                return;

            _listenerCts = new CancellationTokenSource();
            var token = _listenerCts.Token;

            if (showEvent != null)
                Task.Run(() => ListenLoop(showEvent, token, () => ShowOrCreateMainWindow()), token);

            if (exitEvent != null)
                Task.Run(() => ListenLoop(exitEvent, token, () => GetDispatcherQueue()?.TryEnqueue(ExitApplication)), token);
        }

        private static void ListenLoop(EventWaitHandle handle, CancellationToken token, Action action)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!handle.WaitOne(500))
                        continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                    break;

                action();
            }
        }

#if DEBUG
        private void StartDebuggerDetachWatch()
        {
            _debuggerDetachTimer = new Timer(_ =>
            {
                if (Debugger.IsAttached || _isExitingProcess)
                    return;

                GetDispatcherQueue()?.TryEnqueue(() =>
                {
                    if (!_isExitingProcess)
                        ExitApplication();
                });
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
#endif

        private DispatcherQueue? GetDispatcherQueue()
        {
            try
            {
                return DispatcherQueue.GetForCurrentThread();
            }
            catch
            {
                return null;
            }
        }

        private static void BringWindowToForeground(Window window)
        {
            window.AppWindow.IsShownInSwitchers = true;
            window.Activate();
            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != IntPtr.Zero)
                PInvokeHelper.SetForegroundWindow(hwnd);
        }
    }
}
