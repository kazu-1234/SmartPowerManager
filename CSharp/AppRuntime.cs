// v2.2.0

using Microsoft.UI.Xaml;
using SmartPowerManager.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using WinUiShared;

namespace SmartPowerManager
{
    /// <summary>
    /// プロセス寿命: トレイ・スケジュール executor・二重起動イベント。MainWindow は都度生成（ADM 同等）。
    /// 共通処理（二重起動・トレイ・復帰監視・終了の一本化）は WinUiShared.AppRuntimeBase。
    /// ここにはスケジュール実行（executor）と確認ダイアログ、復帰時のヘルスチェックだけを書く。
    /// </summary>
    public sealed class AppRuntime : AppRuntimeBase
    {
        /// <summary>確認ダイアログの表示に失敗したときの再試行回数（待ち時間は 800ms × 試行回数）。</summary>
        private const int PendingConfirmationMaxAttempts = 3;
        private const int PendingConfirmationRetryBaseMs = 800;

        private readonly Settings _settings;
        private readonly ScheduleManager _scheduleManager;
        private readonly SyncCoordinatorService _syncCoordinator;
        private readonly AppState _appState;
        private readonly ScheduleExecutorService _executor;
        private readonly ConfirmationDialogService _confirmationDialog;

        private bool _executorInitialized;

        public AppRuntime(Application app, Settings settings)
            : base(app, settings)
        {
            _settings = settings;
            _scheduleManager = new ScheduleManager();
            _syncCoordinator = new SyncCoordinatorService();
            _appState = new AppState(_settings, _scheduleManager, _syncCoordinator);

            _confirmationDialog = new ConfirmationDialogService(UiDispatcher);
            _executor = new ScheduleExecutorService(
                _scheduleManager,
                _syncCoordinator,
                _confirmationDialog,
                UiDispatcher,
                _settings);
            _appState.Executor = _executor;
            _appState.ApplyTrayIconVisibility = ApplyTrayIconVisibility;
            _appState.RequestSharedScheduleRefresh = RequestSharedScheduleRefresh;

            WireExecutorEvents();
        }

        public AppState AppState => _appState;
        public Settings Settings => _settings;

        protected override Window CreateMainWindow() => new MainWindow(this);

        /// <summary>コア（スケジュール監視）をトレイより先に初期化し、ログオン直後の再適用期間を始める。</summary>
        protected override void OnStartCore()
        {
            EnsureExecutor();
            // ログオン直後は unlock イベントが来ないことが多いので、起動パスでも遅延再評価を回す
            ResumeCoordinator?.BeginStartupPeriod();
        }

        protected override void OnExitCore()
        {
            _executor.Dispose();
        }

        public void RequestSharedScheduleRefresh()
        {
            UiDispatcher.TryEnqueue(() => (CurrentMainWindow as MainWindow)?.RefreshCurrentPage());
        }

        private void WireExecutorEvents()
        {
            // executor のイベントはタイマー（BG スレッド）から来るので、UI 更新は必ず UI スレッドへ積む
            _executor.LogAdded += msg =>
                UiDispatcher.TryEnqueue(() => _appState.AddActivityLog(msg));
            _executor.SchedulesChanged += () =>
                UiDispatcher.TryEnqueue(RequestSharedScheduleRefresh);
            _executor.MonitoringStateChanged += () =>
                UiDispatcher.TryEnqueue(RequestSharedScheduleRefresh);
            _executor.ShowWindowRequested += () =>
                UiDispatcher.TryEnqueue(() => ShowOrCreateMainWindow());
            _executor.PendingConfirmationRequested += () =>
                UiDispatcher.TryEnqueue(HandlePendingConfirmationAsync);
            _syncCoordinator.LogAdded += msg =>
                UiDispatcher.TryEnqueue(() => _appState.AddStartupLog(msg));
        }

        private void HandlePendingConfirmationAsync()
        {
            UiDispatcher.TryEnqueue(async () =>
            {
                for (int attempt = 1; attempt <= PendingConfirmationMaxAttempts; attempt++)
                {
                    if (IsExitingProcess)
                        return;

                    try
                    {
                        await _executor.HandlePendingActionAsync();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Pending confirmation failed (attempt {attempt}): {ex.Message}");
                        if (attempt >= PendingConfirmationMaxAttempts)
                        {
                            _appState.AddActivityLog("確認ダイアログの表示に失敗しました");
                            return;
                        }

                        await Task.Delay(PendingConfirmationRetryBaseMs * attempt);
                    }
                }
            });
        }

        // ───────── スリープ復帰（トレイ・監視窓の作り直しは基底クラスが先に行う） ─────────

        protected override void OnResumeApplyCore()
        {
            if (!_executorInitialized)
                return;

            _executor.EnsureHealthy(announce: false);
        }

        /// <summary>UI スレッドへ積めなかったとき（任意スレッド）。スケジュールの評価だけ行う。</summary>
        protected override void OnResumeApplyDirectCore()
        {
            if (!_executorInitialized)
                return;

            _executor.EvaluateScheduleNow();
        }

        protected override bool UseResumeWatchdog => true;

        protected override void OnResumeWatchdogCore()
        {
            if (!_executorInitialized)
                return;

            _executor.EnsureHealthy(announce: false, evaluateSchedule: true);
        }

        private void EnsureExecutor()
        {
            if (_executorInitialized)
                return;

            _executorInitialized = true;
            _executor.Initialize();
        }
    }
}
