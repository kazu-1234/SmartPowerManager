using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
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
        private CancellationTokenSource? _listenerCts;
        private CancellationTokenSource? _startupHealthCts;
        private Timer? _healthThreadingWatchdog;
        private DateTime _intensiveHealthUntil = DateTime.MinValue;
        private bool _trayInitialized;
        private bool _executorInitialized;
        private bool _systemEventInitialized;
        private bool _isExitingProcess;
        private bool _startupUpdateCheckScheduled;
#if DEBUG
        private Timer? _debuggerDetachTimer;
#endif

        private const int IntensiveHealthSeconds = 120;
        private const int HealthWatchdogNormalIntervalMs = 30000;
        private const int HealthWatchdogIntensiveIntervalMs = 2500;

        /// <summary>開始時点からの絶対遅延（累積 Delay にしない）。BlueShift と同型。</summary>
        private static readonly int[] HealthCheckDelaysMs =
            { 800, 2000, 5000, 15000, 30000, 60000, 90000 };

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
            EnsureSystemEventMonitor();
            // T+0 Force 相当（ログオン直後のタイマー未 Tick を即時救済）
            _executor.EnsureHealthy(announce: false);
            BeginIntensiveHealthPeriod();
            ScheduleDelayedHealthChecks();

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

        public void ExitApplication()
        {
            if (_isExitingProcess)
                return;

            _isExitingProcess = true;
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _listenerCts = null;
            _startupHealthCts?.Cancel();
            _startupHealthCts?.Dispose();
            _startupHealthCts = null;
            _healthThreadingWatchdog?.Dispose();
            _healthThreadingWatchdog = null;
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

        /// <summary>
        /// ログオン直後・スリープ復帰時など、デスクトップ／タイマーが不安定なときに
        /// BlueShift と同型で T+0.8/2/5/15/30/60/90 秒後にヘルスチェックする。
        /// </summary>
        private void ScheduleDelayedHealthChecks()
        {
            _startupHealthCts?.Cancel();
            _startupHealthCts?.Dispose();
            _startupHealthCts = new CancellationTokenSource();
            var token = _startupHealthCts.Token;

            Task.Run(async () =>
            {
                var start = DateTime.UtcNow;
                foreach (int delayMs in HealthCheckDelaysMs)
                {
                    try
                    {
                        var wait = start.AddMilliseconds(delayMs) - DateTime.UtcNow;
                        if (wait > TimeSpan.Zero)
                            await Task.Delay(wait, token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested || _isExitingProcess)
                        break;

                    int capturedDelay = delayMs;
                    if (!(GetDispatcherQueue()?.TryEnqueue(() =>
                        {
                            if (_isExitingProcess || !_executorInitialized)
                                return;
                            EnsureSystemEventMonitor();
                            _executor.EnsureHealthy(announce: capturedDelay >= 90000);
                        }) ?? false))
                    {
                        if (!_isExitingProcess && _executorInitialized)
                            _executor.EvaluateScheduleNow();
                    }
                }
            }, token);
        }

        /// <summary>スリープ復帰・画面復帰・セッション解除時に監視タイマーを再始動する。</summary>
        private void OnSystemResume()
        {
            if (_isExitingProcess || !_executorInitialized)
                return;

            _executor.EnsureHealthy(announce: false);
            BeginIntensiveHealthPeriod();
            ScheduleDelayedHealthChecks();
        }

        /// <summary>専用スレッドからの復帰通知。Dispatcher 不通時は評価のみ直接実行する。</summary>
        private void OnSystemDisplayStateChanged()
        {
            if (!(GetDispatcherQueue()?.TryEnqueue(OnSystemResume) ?? false))
                OnSystemResumeFallbackDirect();
        }

        private void OnSystemResumeFallbackDirect()
        {
            if (_isExitingProcess || !_executorInitialized)
                return;

            // Dispatcher 不通時は評価のみ直接実行（DispatcherTimer は UI スレッド専用）
            _executor.EvaluateScheduleNow();
            BeginIntensiveHealthPeriod();
            ScheduleDelayedHealthChecks();

            // Dispatcher 復帰後に EnsureHealthy（Stop→Start）を再試行
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 10 && !_isExitingProcess; i++)
                {
                    try
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (GetDispatcherQueue()?.TryEnqueue(() =>
                        {
                            if (_isExitingProcess || !_executorInitialized)
                                return;
                            _executor.EnsureHealthy(announce: false);
                        }) == true)
                    {
                        return;
                    }
                }
            });
        }

        private void BeginIntensiveHealthPeriod()
        {
            _intensiveHealthUntil = DateTime.UtcNow.AddSeconds(IntensiveHealthSeconds);
            StartThreadingHealthWatchdog();
        }

        /// <summary>DispatcherTimer が止まっても EnsureHealthy できる Threading.Timer バックアップ（BlueShift 同型）。</summary>
        private void StartThreadingHealthWatchdog()
        {
            int intervalMs = DateTime.UtcNow < _intensiveHealthUntil
                ? HealthWatchdogIntensiveIntervalMs
                : HealthWatchdogNormalIntervalMs;

            if (_healthThreadingWatchdog == null)
            {
                _healthThreadingWatchdog = new Timer(
                    _ =>
                    {
                        if (!(GetDispatcherQueue()?.TryEnqueue(() =>
                            {
                                if (_isExitingProcess || !_executorInitialized)
                                    return;
                                EnsureSystemEventMonitor();
                                // 評価はしない（同一分の再発火防止）。タイマー生存のみ保証する。
                                _executor.EnsureHealthy(announce: false, evaluateSchedule: false);
                                SyncThreadingHealthWatchdogInterval();
                            }) ?? false))
                        {
                            // タイマー再始動は UI 必須。評価だけフォールバック。
                            if (!_isExitingProcess && _executorInitialized
                                && DateTime.UtcNow < _intensiveHealthUntil)
                                _executor.EvaluateScheduleNow();
                        }
                    },
                    null,
                    intervalMs,
                    intervalMs);
            }
            else
            {
                _healthThreadingWatchdog.Change(intervalMs, intervalMs);
            }
        }

        private void SyncThreadingHealthWatchdogInterval()
        {
            if (_healthThreadingWatchdog == null)
                return;

            int intervalMs = DateTime.UtcNow < _intensiveHealthUntil
                ? HealthWatchdogIntensiveIntervalMs
                : HealthWatchdogNormalIntervalMs;
            _healthThreadingWatchdog.Change(intervalMs, intervalMs);
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

        private void EnsureSystemEventMonitor()
        {
            if (_systemEventInitialized)
                return;

            try
            {
                _systemEventWindow = new SystemEventWindow();
                _systemEventWindow.SystemDisplayStateChanged += OnSystemDisplayStateChanged;
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
                Task.Run(() => ListenShowLoop(showEvent, token, () => ShowOrCreateMainWindow()), token);

            if (exitEvent != null)
                Task.Run(() => ListenLoop(exitEvent, token, () => GetDispatcherQueue()?.TryEnqueue(ExitApplication)), token);
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

        private DispatcherQueue GetDispatcherQueue() => _uiDispatcher;

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
