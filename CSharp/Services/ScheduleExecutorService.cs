using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SmartPowerManager.Services;

public sealed class ScheduleExecutorService : IDisposable
{
    private readonly ScheduleManager _scheduleManager;
    private readonly SyncCoordinatorService _syncCoordinator;
    private readonly ConfirmationDialogService _confirmationDialog;
    private readonly Settings _settings;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _processedAutoWolKeys = new();
    private bool _isHandlingPending;
    private int _lastMinute = -1;

    public event Action<string>? LogAdded;
    public event Action? SchedulesChanged;
    public event Action? PendingConfirmationRequested;
    public event Action? ShowWindowRequested;
    public event Action? MonitoringStateChanged;

    public ScheduleExecutorService(
        ScheduleManager scheduleManager,
        SyncCoordinatorService syncCoordinator,
        ConfirmationDialogService confirmationDialog,
        DispatcherQueue _,
        Settings settings)
    {
        _scheduleManager = scheduleManager;
        _syncCoordinator = syncCoordinator;
        _confirmationDialog = confirmationDialog;
        _settings = settings;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += Timer_Tick;
    }

    public bool IsMonitoring(string action) => action switch
    {
        AppConstants.ActionShutdown => _settings.MonitoringEnabledShutdown,
        AppConstants.ActionRestart => _settings.MonitoringEnabledRestart,
        _ => false
    };

    /// <summary>タイマーを開始し、設定に応じた初期状態を適用する。</summary>
    public void Initialize()
    {
        if (!_timer.IsEnabled)
            _timer.Start();

        bool anyOn = IsMonitoring(AppConstants.ActionShutdown) || IsMonitoring(AppConstants.ActionRestart);
        if (DiscardElapsedForDisabledActions())
            SchedulesChanged?.Invoke();

        if (anyOn)
            LogAdded?.Invoke("スケジュール監視を開始しました");
        else
            LogAdded?.Invoke("スケジュール監視はオフです（オフの種別は実行せず、過ぎた予定は削除します）");

        MonitoringStateChanged?.Invoke();
    }

    public void SetMonitoring(string action, bool enabled)
    {
        bool current = IsMonitoring(action);
        if (current == enabled)
            return;

        if (action == AppConstants.ActionShutdown)
            _settings.MonitoringEnabledShutdown = enabled;
        else if (action == AppConstants.ActionRestart)
            _settings.MonitoringEnabledRestart = enabled;
        else
            return;

        _settings.Save();

        string label = action == AppConstants.ActionShutdown ? "シャットダウン" : "再起動";
        if (enabled)
        {
            // 再開前に、オフ中に過ぎた予定を実行せず除去
            DiscardElapsedForAction(action);
            LogAdded?.Invoke($"{label}のスケジュール監視を開始しました");
        }
        else
        {
            ClearPendingIfAction(action);
            DiscardElapsedForAction(action);
            LogAdded?.Invoke($"{label}のスケジュール監視を停止しました");
        }

        MonitoringStateChanged?.Invoke();
        SchedulesChanged?.Invoke();
    }

    public async Task HandlePendingActionAsync()
    {
        if (_isHandlingPending)
            return;

        var pending = _scheduleManager.PendingAction;
        if (pending == null)
            return;

        if (!IsMonitoring(pending.Action))
        {
            _scheduleManager.ClearPendingAction();
            return;
        }

        _isHandlingPending = true;
        try
        {
            _scheduleManager.ClearPendingAction();
            bool confirmed = await _confirmationDialog.ShowConfirmationAsync(pending.Action, pending.TriggerLabel);
            if (!IsMonitoring(pending.Action))
            {
                LogAdded?.Invoke("監視オフのため実行をキャンセルしました");
                return;
            }

            if (confirmed)
            {
                PowerStateHelper.ExecuteShutdownOrRestart(pending.Action);
                string label = pending.Action == AppConstants.ActionShutdown ? "シャットダウン" : "再起動";
                LogAdded?.Invoke($"{label}コマンドを送信しました");
            }
            else
            {
                LogAdded?.Invoke("ユーザーによりキャンセルされました");
            }
        }
        finally
        {
            _isHandlingPending = false;
        }
    }

    private void Timer_Tick(object? sender, object e)
    {
        CheckShowSignal();

        var now = DateTime.Now;
        if (now.Minute == _lastMinute)
            return;

        _lastMinute = now.Minute;

        // 過ぎた予定の破棄は自動（監視オフ種別＋発火枠を過ぎた一回限りは常に削除・未実行）
        bool changed = DiscardElapsedForDisabledActions();
        changed |= DiscardStaleOnetimes();

        bool anyMonitoring = IsMonitoring(AppConstants.ActionShutdown) || IsMonitoring(AppConstants.ActionRestart);
        if (anyMonitoring)
        {
            CheckAutoWol(now);
            bool triggered = _scheduleManager.CheckAndExecute(
                IsMonitoring,
                msg => LogAdded?.Invoke(msg));
            if (triggered)
            {
                changed = true;
                if (_scheduleManager.PendingAction != null)
                    PendingConfirmationRequested?.Invoke();
            }
        }

        if (changed)
            SchedulesChanged?.Invoke();
    }

    private bool DiscardElapsedForDisabledActions()
    {
        bool changed = false;
        if (!IsMonitoring(AppConstants.ActionShutdown))
            changed |= DiscardElapsedForAction(AppConstants.ActionShutdown);
        if (!IsMonitoring(AppConstants.ActionRestart))
            changed |= DiscardElapsedForAction(AppConstants.ActionRestart);
        return changed;
    }

    /// <summary>発火枠（60秒）を過ぎた一回限りは、監視の有無に関係なく実行せず削除する。</summary>
    private bool DiscardStaleOnetimes() =>
        _scheduleManager.DiscardStaleOnetimes(msg => LogAdded?.Invoke(msg));

    private bool DiscardElapsedForAction(string action) =>
        _scheduleManager.DiscardElapsedSchedules(action, msg => LogAdded?.Invoke(msg));

    private void ClearPendingIfAction(string action)
    {
        if (_scheduleManager.PendingAction?.Action == action)
            _scheduleManager.ClearPendingAction();
    }

    private void CheckShowSignal()
    {
        if (!File.Exists(AppPaths.SignalFilePath))
            return;

        try
        {
            File.Delete(AppPaths.SignalFilePath);
            ShowWindowRequested?.Invoke();
        }
        catch
        {
        }
    }

    private void CheckAutoWol(DateTime now)
    {
        foreach (var evt in _scheduleManager.GetAllNextEvents())
        {
            if (!IsMonitoring(evt.Action))
                continue;

            var wolTime = evt.DateTime.AddMinutes(-3);
            if (Math.Abs((now - wolTime).TotalSeconds) >= 60)
                continue;

            string key = $"{evt.Action}:{evt.DateTime:yyyy-MM-dd HH:mm}:wol";
            if (!_processedAutoWolKeys.Add(key))
                continue;

            _ = _syncCoordinator.SyncToDevicesAsync(_scheduleManager, silent: true);
            LogAdded?.Invoke($"3分前 WoL 同期 ({evt.Action})");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
