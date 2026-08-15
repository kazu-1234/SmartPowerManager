using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SmartPowerManager.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;
using WinUiShared;

namespace SmartPowerManager
{
    /// <summary>
    /// プロセス寿命: トレイ・スケジュール executor・二重起動イベント。MainWindow は都度生成（ADM 同等）。
    /// </summary>
    public sealed class AppRuntime : IDisposable
    {
        private readonly Application _app;
        private readonly DispatcherQueue _uiDispatcher;
        private readonly Settings _settings;
        private readonly ScheduleManager _scheduleManager;
        private readonly SyncCoordinatorService _syncCoordinator;
        private readonly AppState _appState;
        private readonly ScheduleExecutorService _executor;
        private readonly ConfirmationDialogService _confirmationDialog;

        private MainWindow? _mainWindow;
        private TrayMessageWindow? _trayMessageWindow;
        private SystemEventWindow? _systemEventWindow;
        private ResumeReapplyCoordinator? _resumeCoordinator;
        private CancellationTokenSource? _listenerCts;
        private bool _trayInitialized;
        private bool _executorInitialized;
        private bool _systemEventInitialized;
        private bool _isExitingProcess;
        private bool _startupUpdateCheckScheduled;
        private bool _startupReadyNotified;
#if DEBUG
        private Timer? _debuggerDetachTimer;
#endif

        public AppRuntime(Application app, Settings settings)
        {
            _app = app;
            // 二重起動リスナーは BG スレッドから来るため、UI Dispatcher を起動時に保持する
            _uiDispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("AppRuntime must be created on the UI thread.");
            _settings = settings;
            _scheduleManager = new ScheduleManager();
            _syncCoordinator = new SyncCoordinatorService();
            _appState = new AppState(_settings, _scheduleManager, _syncCoordinator);

            _confirmationDialog = new ConfirmationDialogService(_uiDispatcher);
            _executor = new ScheduleExecutorService(
                _scheduleManager,
                _syncCoordinator,
                _confirmationDialog,
                _uiDispatcher,
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
            EnsureResumeCoordinator();
            EnsureSystemEventMonitor();
            _resumeCoordinator?.BeginStartupPeriod();

            if (!ShouldUseTray())
            {
#if DEBUG
                if (Debugger.IsAttached)
                    StartDebuggerDetachWatch();
#endif
                if (requestInteractiveShow || !launchInBackground)
                    ShowOrCreateMainWindow();
                ScheduleStartupReadyNotification();
                return;
            }

            EnsureTray();

            if (requestInteractiveShow || !launchInBackground)
                ShowOrCreateMainWindow();

            ScheduleStartupReadyNotification();
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
            ScheduleStartupUpdateCheckIfNeeded();
        }

        private void ScheduleStartupUpdateCheckIfNeeded()
        {
            if (_startupUpdateCheckScheduled || _mainWindow == null)
                return;

            _startupUpdateCheckScheduled = true;
            _ = UpdateFlowService.TryStartupCheckAsync(_mainWindow, _settings);
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

        public void ExitApplication(string reason = "unknown")
        {
            if (_isExitingProcess)
                return;

            _isExitingProcess = true;
            AppendLifetimeLog(reason);
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _listenerCts = null;
            _resumeCoordinator?.Dispose();
            _resumeCoordinator = null;
#if DEBUG
            _debuggerDetachTimer?.Dispose();
            _debuggerDetachTimer = null;
#endif

            _systemEventWindow?.Dispose();
            _systemEventWindow = null;
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

        public void Dispose() => ExitApplication("dispose");

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
                const int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    if (_isExitingProcess)
                        return;

                    try
                    {
                        ShowOrCreateMainWindowCore();
                        await _executor.HandlePendingActionAsync();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Pending confirmation failed (attempt {attempt}): {ex.Message}");
                        if (attempt >= maxAttempts)
                        {
                            _appState.AddActivityLog("確認ダイアログの表示に失敗しました");
                            return;
                        }

                        await Task.Delay(800 * attempt);
                    }
                }
            });
        }

        private void OnResumeApply()
        {
            if (_isExitingProcess || !_executorInitialized)
                return;

            try
            {
                EnsureResidentLifetime();
                _executor.EnsureHealthy(announce: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnResumeApply failed: {ex.Message}");
            }
        }

        private void OnResumeApplyDirect()
        {
            if (_isExitingProcess || !_executorInitialized)
                return;

            _executor.EvaluateScheduleNow();
        }

        private void OnResumeWatchdog()
        {
            if (_isExitingProcess || !_executorInitialized)
                return;

            EnsureResidentLifetime();
            _executor.EnsureHealthy(announce: false, evaluateSchedule: false);
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

        private void EnsureResumeCoordinator()
        {
            if (_resumeCoordinator != null)
                return;

            _resumeCoordinator = new ResumeReapplyCoordinator(
                _uiDispatcher,
                () => _isExitingProcess,
                OnResumeApply,
                OnResumeApplyDirect,
                OnResumeWatchdog);
            _resumeCoordinator.Start();
        }

        private void EnsureSystemEventMonitor()
        {
            if (_systemEventInitialized)
            {
                if (_systemEventWindow is { IsAlive: true })
                    return;

                _systemEventWindow?.Dispose();
                _systemEventWindow = null;
                _systemEventInitialized = false;
            }

            try
            {
                EnsureResumeCoordinator();
                _systemEventWindow = new SystemEventWindow("SmartPowerManagerSystemEventWindow_v2");
                _systemEventWindow.SystemDisplayStateChanged += () => _resumeCoordinator?.NotifyResume();
                _systemEventWindow.SystemSuspending += () => _resumeCoordinator?.NotifySuspend();
                _systemEventInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"System event monitor init failed: {ex.Message}");
                _systemEventWindow?.Dispose();
                _systemEventWindow = null;
                _systemEventInitialized = false;
            }
        }

        private void EnsureTrayAlive(bool reAddIcon = false)
        {
            if (_isExitingProcess || !ShouldUseTray())
                return;

            if (_trayInitialized && _trayMessageWindow is { IsAlive: true })
            {
                if (reAddIcon && !_settings.HideTrayIcon)
                {
                    try { _trayMessageWindow.TrayIcon.ReAdd(); }
                    catch (Exception ex) { Debug.WriteLine($"Tray ReAdd failed: {ex.Message}"); }
                }
                return;
            }

            _trayMessageWindow?.Dispose();
            _trayMessageWindow = null;
            _trayInitialized = false;
            EnsureTray();
        }

        private void EnsureResidentLifetime()
        {
            EnsureSystemEventMonitor();
            EnsureTrayAlive(reAddIcon: true);
        }

        /// <summary>コア初期化後にトレイバルーンで常駐準備完了を知らせる（VS 外での動作確認用）。</summary>
        private void ScheduleStartupReadyNotification()
        {
            if (_startupReadyNotified)
                return;
            _startupReadyNotified = true;

            _ = Task.Run(async () =>
            {
                int[] delaysMs = { 1500, 2500, 5000 };
                var start = DateTime.UtcNow;
                foreach (int delayMs in delaysMs)
                {
                    try
                    {
                        var wait = start.AddMilliseconds(delayMs) - DateTime.UtcNow;
                        if (wait > TimeSpan.Zero)
                            await Task.Delay(wait).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (_isExitingProcess)
                        return;

                    var shown = new TaskCompletionSource<bool>();
                    if (!(GetDispatcherQueue()?.TryEnqueue(() =>
                        {
                            try
                            {
                                if (_isExitingProcess || _settings.HideTrayIcon
                                    || !_trayInitialized || _trayMessageWindow == null)
                                {
                                    shown.TrySetResult(false);
                                    return;
                                }

                                _trayMessageWindow.TrayIcon.ShowBalloon(
                                    Strings.Get("Tray_StartupReadyTitle"),
                                    Strings.Get("Tray_StartupReadyBody"));
                                shown.TrySetResult(true);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Startup ready balloon failed: {ex.Message}");
                                shown.TrySetResult(false);
                            }
                        }) ?? false))
                    {
                        shown.TrySetResult(false);
                    }

                    if (await shown.Task.ConfigureAwait(false))
                        return;
                }
            });
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
                _trayMessageWindow.TrayIcon.ExitRequested += () => GetDispatcherQueue()?.TryEnqueue(() => ExitApplication("tray-menu"));
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
                Task.Run(() => ListenShowLoop(showEvent, token, () => ShowOrCreateMainWindow()), token);

            if (exitEvent != null)
                Task.Run(() => ListenLoop(exitEvent, token, () => GetDispatcherQueue()?.TryEnqueue(() => ExitApplication("exit-signal"))), token);
        }

        /// <summary>Event に加え、ファイル show_signal も 500ms ポールで拾う（DispatcherTimer 停止中の二次起動対策）。</summary>
        private static void ListenShowLoop(EventWaitHandle handle, CancellationToken token, Action action)
        {
            while (!token.IsCancellationRequested)
            {
                bool signaled = false;
                try
                {
                    signaled = handle.WaitOne(500);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (!signaled)
                    signaled = SingleInstanceManager.TryConsumeShowSignal();

                if (token.IsCancellationRequested)
                    break;

                if (signaled)
                    action();
            }
        }

        private static void ListenLoop(EventWaitHandle handle, CancellationToken token, Action action)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    handle.WaitOne(500);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                    break;

                if (SingleInstanceManager.TryConsumeExitSignal())
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
                        ExitApplication("debug-detach");
                });
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
#endif

        private DispatcherQueue GetDispatcherQueue() => _uiDispatcher;

        internal static void AppendLifetimeLog(string reason)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.AppDataDirectory);
                File.AppendAllText(
                    Path.Combine(AppPaths.AppDataDirectory, "lifetime.log"),
                    $"{DateTime.UtcNow:O} {reason}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private static void BringWindowToForeground(Window window)
        {
            try
            {
                if (window.AppWindow.Presenter is OverlappedPresenter presenter
                    && presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }

                window.AppWindow.IsShownInSwitchers = true;
                window.AppWindow.Show();
                window.Activate();

                IntPtr hwnd = WindowNative.GetWindowHandle(window);
                if (hwnd != IntPtr.Zero)
                    PInvokeHelper.SetForegroundWindow(hwnd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BringWindowToForeground failed: {ex.Message}");
            }
        }
    }
}
