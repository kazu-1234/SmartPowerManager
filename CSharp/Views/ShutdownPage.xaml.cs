using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SmartPowerManager.Services;

namespace SmartPowerManager.Views;

public sealed partial class ShutdownPage : Page, ISchedulePage
{
    private AppState? _state;
    private bool _isInitializing;
    private RealtimeClockService.Tracker? _dailyTracker;
    private RealtimeClockService.Tracker? _weeklyTracker;
    private RealtimeClockService.Tracker? _onetimeTracker;
    private bool _dailyRealtimeActive = true;
    private bool _suppressMonitorToggle;
    private const string Action = AppConstants.ActionShutdown;

    public ShutdownPage()
    {
        InitializeComponent();
        SchedulePageFillLayout.Attach(this, PageRoot, PageTitleText, ScheduleSplitGrid, LeftCard, RightCard);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _state = e.Parameter as AppState;
        _isInitializing = true;

        foreach (string day in AppConstants.WeekdaysJp)
            WeeklyWeekdayBox.Items.Add(day);
        WeeklyWeekdayBox.SelectedIndex = 0;

        OnetimeDateTimeInput.DateTime = System.DateTime.Now;
        DailyTimeInput.Time = new TimeSpan(23, 0, 0);
        WeeklyTimeInput.Time = new TimeSpan(23, 0, 0);

        RightCard.Bind(_state!);
        RefreshDisplay();
        RefreshMonitorUi();
        SetupRealtimeTracking();
        _isInitializing = false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopRealtimeTracking();
        base.OnNavigatedFrom(e);
    }

    private void StopRealtimeTracking()
    {
        RealtimeClockService.Untrack(_dailyTracker);
        RealtimeClockService.Untrack(_weeklyTracker);
        RealtimeClockService.Untrack(_onetimeTracker);
        _dailyTracker = null;
        _weeklyTracker = null;
        _onetimeTracker = null;
    }

    private void SetupRealtimeTracking()
    {
        StopRealtimeTracking();
        _dailyTracker = RealtimeClockService.Track(DailyTimeInput, () => _dailyRealtimeActive);
        _weeklyTracker = RealtimeClockService.Track(WeeklyTimeInput);
        _onetimeTracker = RealtimeClockService.Track(OnetimeDateTimeInput.TimeInputControl);
    }

    public void RefreshDisplay()
    {
        if (_state == null)
            return;

        var daily = _state.ScheduleManager.GetDaily(Action);
        DailyToggle.IsOn = daily.Enabled;
        DailyTimeInput.Time = new TimeSpan(daily.Hour, daily.Minute, 0);
        _dailyRealtimeActive = !daily.Enabled;

        ScheduleActionPageHelper.RefreshNextSchedule(NextScheduleText, _state.ScheduleManager, Action);
        if (!(_state.Executor?.IsMonitoring(Action) ?? false))
            NextScheduleText.Text = "監視オフ（予定は実行されません）";

        RightCard.Refresh();
        RefreshMonitorUi();
    }

    private void RefreshMonitorUi()
    {
        bool monitoring = _state?.Executor?.IsMonitoring(Action) ?? false;
        MonitorStatusText.Text = monitoring ? "監視中" : "停止中";

        if (MonitorToggle.IsOn == monitoring)
            return;

        _suppressMonitorToggle = true;
        try
        {
            MonitorToggle.IsOn = monitoring;
        }
        finally
        {
            _suppressMonitorToggle = false;
        }
    }

    private void MonitorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _suppressMonitorToggle || _state?.Executor == null)
            return;

        _state.Executor.SetMonitoring(Action, MonitorToggle.IsOn);
        RefreshMonitorUi();
        RefreshDisplay();
    }

    private async void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null || sender is not Button btn || btn.Tag is not string tag)
            return;

        await ScheduleActionPageHelper.QuickHoursLaterAsync(_state, Action, int.Parse(tag), RefreshAfterChange);
    }

    private async void DailyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _state == null)
            return;

        var time = DailyTimeInput.Time;
        _dailyRealtimeActive = !DailyToggle.IsOn;

        bool ok = await ScheduleActionPageHelper.SetDailyWithConflictCheckAsync(
            XamlRoot, _state, Action, DailyToggle.IsOn, time.Hours, time.Minutes, RefreshAfterChange);

        if (!ok)
        {
            _isInitializing = true;
            DailyToggle.IsOn = false;
            _dailyRealtimeActive = true;
            _isInitializing = false;
        }
    }

    private void DailyTimeInput_TimeChanged(object? sender, EventArgs e)
    {
        _dailyTracker?.Stop();
        if (_isInitializing || _state == null || !DailyToggle.IsOn)
            return;

        var time = DailyTimeInput.Time;
        _state.ScheduleManager.SetDaily(Action, true, time.Hours, time.Minutes);
        _ = RefreshAfterChangeAsync();
    }

    private async void AddWeeklyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null)
            return;

        _weeklyTracker?.Stop();
        await ScheduleActionPageHelper.AddWeeklyAsync(
            XamlRoot, _state, Action, WeeklyWeekdayBox, WeeklyTimeInput, RefreshAfterChange);
    }

    private async void AddOnetimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null)
            return;

        _onetimeTracker?.Stop();
        await ScheduleActionPageHelper.AddOnetimeAsync(
            XamlRoot, _state, Action, OnetimeDateTimeInput, RefreshAfterChange);
    }

    private async void CancelNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null)
            return;

        await ScheduleActionPageHelper.CancelNextAsync(_state, Action, RefreshAfterChange);
    }

    private void RefreshAfterChange()
    {
        RefreshDisplay();
        _state?.RequestSharedScheduleRefresh?.Invoke();
    }

    private async Task RefreshAfterChangeAsync()
    {
        RefreshDisplay();
        if (_state != null)
            await _state.SyncDevicesAsync(silent: true);
        RefreshAfterChange();
    }
}
