using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SmartPowerManager.Models;
using SmartPowerManager.Services;

namespace SmartPowerManager.Views;

public sealed partial class WakePage : Page, ISchedulePage
{
    private AppState? _state;
    private bool _isInitializing;
    private RealtimeClockService.Tracker? _dailyTracker;
    private RealtimeClockService.Tracker? _weeklyTracker;
    private RealtimeClockService.Tracker? _onetimeTracker;
    private bool _dailyRealtimeActive = true;

    public WakePage()
    {
        InitializeComponent();
        SchedulePageFillLayout.Attach(this, PageRoot, LeftCard, RightCard, syncLeftAndRightHeights: false);
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
        DailyTimeInput.Time = new TimeSpan(7, 0, 0);
        WeeklyTimeInput.Time = new TimeSpan(7, 0, 0);

        RightCard.Bind(_state!);
        RefreshDisplay();
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

        var daily = _state.ScheduleManager.Data.PicoSettings.StartupDaily;
        DailyToggle.IsOn = daily.Enabled;
        DailyTimeInput.Time = new TimeSpan(daily.Hour, daily.Minute, 0);
        _dailyRealtimeActive = !daily.Enabled;
        RightCard.Refresh();
    }

    private void DailyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _state == null)
            return;

        _dailyRealtimeActive = !DailyToggle.IsOn;

        var time = DailyTimeInput.Time;
        _state.ScheduleManager.SetStartupDaily(DailyToggle.IsOn, time.Hours, time.Minutes);
        _ = RefreshAfterChangeAsync();
    }

    private void DailyTimeInput_TimeChanged(object? sender, EventArgs e)
    {
        _dailyTracker?.Stop();
        if (_isInitializing || _state == null)
            return;

        var time = DailyTimeInput.Time;
        var daily = _state.ScheduleManager.Data.PicoSettings.StartupDaily;
        _state.ScheduleManager.SetStartupDaily(daily.Enabled, time.Hours, time.Minutes);
        _ = RefreshAfterChangeAsync();
    }

    private async void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null || sender is not Button btn || btn.Tag is not string tag)
            return;

        _state.ScheduleManager.AddStartupOnetimeHoursLater(int.Parse(tag));
        _state.AddStartupLog(Strings.Format("Schedule_QuickAdded", tag));
        await RefreshAfterChangeAsync();
    }

    private async void AddWeeklyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null)
            return;

        _weeklyTracker?.Stop();
        int weekday = WeeklyWeekdayBox.SelectedIndex;
        if (weekday < 0) weekday = 0;
        var time = WeeklyTimeInput.Time;
        _state.ScheduleManager.AddStartupWeekly(weekday, time.Hours, time.Minutes);
        await RefreshAfterChangeAsync();
    }

    private async void AddOnetimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null)
            return;

        _onetimeTracker?.Stop();
        var target = OnetimeDateTimeInput.DateTime;
        if (target <= System.DateTime.Now)
            return;

        _state.ScheduleManager.AddStartupOnetime(target.ToString("yyyy-MM-dd HH:mm"));
        await RefreshAfterChangeAsync();
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null)
            return;

        await _state.SyncDevicesAsync();
        RefreshDisplay();
    }

    private async void ClearAllStartupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null)
            return;

        _state.ScheduleManager.ClearAllStartupSchedules();
        _state.AddStartupLog("起動スケジュールをすべてクリアしました");
        await RefreshAfterChangeAsync();
    }

    private async Task RefreshAfterChangeAsync()
    {
        RefreshDisplay();
        if (_state != null)
            await _state.SyncDevicesAsync(silent: true);
        _state?.RequestSharedScheduleRefresh?.Invoke();
    }
}
