using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartPowerManager.Models;
using SmartPowerManager.Services;

namespace SmartPowerManager.Views;

public sealed partial class UpcomingSchedulePanel : UserControl
{
    private AppState? _state;
    private string? _categoryFilter;

    public UpcomingSchedulePanel()
    {
        InitializeComponent();
        double listHeight = AppConstants.ScheduleListVisibleRows * AppConstants.ScheduleEntryRowHeight;
        ScheduleScroll.Height = listHeight;
        ListRow.Height = new GridLength(listHeight);
    }

    /// <summary>
    /// 表示カテゴリを絞る（shutdown / restart / wake）。null で全件。
    /// </summary>
    public string? CategoryFilter
    {
        get => _categoryFilter;
        set
        {
            _categoryFilter = value;
            Refresh();
        }
    }

    public void Bind(AppState state, string? categoryFilter = null)
    {
        _state = state;
        _categoryFilter = categoryFilter;
        Refresh();
    }

    public void Refresh()
    {
        if (_state == null)
            return;

        Func<string, bool>? isVisible = null;
        if (_state.Executor != null)
        {
            var executor = _state.Executor;
            isVisible = action => executor.IsMonitoring(action);
        }

        var entries = ScheduleOverviewHelper.BuildUpcomingEntries(
            _state.ScheduleManager,
            _categoryFilter,
            isVisible);
        ScheduleList.ItemsSource = entries;
        bool empty = entries.Count == 0;
        EmptyStateText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ScheduleScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        // 7件以下で見切れていなければリスト内スクロール不可
        bool needsScroll = entries.Count > AppConstants.ScheduleListVisibleRows;
        ScheduleScroll.VerticalScrollMode = needsScroll ? ScrollMode.Enabled : ScrollMode.Disabled;
        ScheduleScroll.VerticalScrollBarVisibility = needsScroll
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Hidden;
        if (!needsScroll)
            ScheduleScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private async void RemoveEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null || sender is not Button { Tag: UpcomingScheduleEntry entry })
            return;

        var manager = _state.ScheduleManager;

        switch (entry.EntryKind)
        {
            case "onetime" when entry.ScheduleId != null:
                manager.RemoveOnetime(entry.ScheduleId);
                break;
            case "weekly" when entry.SortKey.HasValue:
            case "daily" when entry.SortKey.HasValue:
            case "wake_daily" when entry.SortKey.HasValue:
            case "wake_weekly" when entry.SortKey.HasValue:
                manager.SkipDateTime(entry.SortKey.Value.ToString("yyyy-MM-dd HH:mm"));
                break;
            case "wake_onetime" when entry.ScheduleId != null:
                manager.RemoveStartupOnetime(entry.ScheduleId);
                break;
        }

        _state.AddActivityLog($"予定をスキップ/削除: {entry.Description} {entry.DateTimeText}");
        await _state.SyncDevicesAsync(silent: true);
        NotifySchedulesChanged();
    }

    private async void RemoveAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null || sender is not Button { Tag: UpcomingScheduleEntry entry })
            return;

        var manager = _state.ScheduleManager;

        switch (entry.EntryKind)
        {
            case "weekly" when entry.ScheduleId != null:
                manager.RemoveWeekly(entry.ScheduleId);
                break;
            case "daily" when entry.Action != null:
            {
                var daily = manager.GetDaily(entry.Action);
                manager.SetDaily(entry.Action, enabled: false, daily.Hour, daily.Minute);
                break;
            }
            case "wake_weekly" when entry.ScheduleId != null:
                manager.RemoveStartupWeekly(entry.ScheduleId);
                break;
            case "wake_daily":
            {
                var daily = manager.Data.PicoSettings.StartupDaily;
                manager.SetStartupDaily(enabled: false, daily.Hour, daily.Minute);
                break;
            }
            default:
                return;
        }

        _state.AddActivityLog($"スケジュールをすべて削除: {entry.Description}");
        await _state.SyncDevicesAsync(silent: true);
        NotifySchedulesChanged();
    }

    private void NotifySchedulesChanged()
    {
        // ページ側のトグル等も同期させる（自身の Refresh はページ RefreshDisplay 経由）
        _state?.RequestSharedScheduleRefresh?.Invoke();
    }
}
