using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartPowerManager.Models;
using SmartPowerManager.Services;

namespace SmartPowerManager.Views;

internal static class ScheduleActionPageHelper
{
    public static void RefreshNextSchedule(TextBlock target, ScheduleManager manager, string action)
    {
        var next = manager.GetNextEventForAction(action);
        if (next == null)
        {
            target.Text = Strings.Get("Schedule_NoNext");
            return;
        }

        string actionLabel = action == AppConstants.ActionShutdown
            ? Strings.Get("Action_Shutdown")
            : Strings.Get("Action_Restart");
        target.Text = string.Format(Strings.Get("Schedule_NextFormat"),
            actionLabel,
            next.DateTime.ToString("yyyy-MM-dd HH:mm"),
            next.ScheduleType);
    }

    public static async Task<bool> SetDailyWithConflictCheckAsync(
        XamlRoot xamlRoot,
        AppState state,
        string action,
        bool enabled,
        int hour,
        int minute,
        Action refresh)
    {
        if (enabled)
        {
            string? conflict = state.ScheduleManager.CheckConflict(action, "daily", new ConflictTimeInfo
            {
                Hour = hour,
                Minute = minute
            });

            if (conflict != null)
            {
                await ShowErrorAsync(xamlRoot, Strings.Format("Schedule_Conflict", conflict));
                return false;
            }
        }

        state.ScheduleManager.SetDaily(action, enabled, hour, minute);
        state.AddActivityLog(enabled
            ? $"毎日スケジュールを有効化: {hour:00}:{minute:00}"
            : "毎日スケジュールを無効化しました");
        await state.SyncDevicesAsync(silent: true);
        refresh();
        return true;
    }

    public static async Task AddWeeklyAsync(
        XamlRoot xamlRoot,
        AppState state,
        string action,
        ComboBox weekdayBox,
        CompactTimeInput timeInput,
        Action refresh)
    {
        int weekday = weekdayBox.SelectedIndex;
        if (weekday < 0)
            weekday = 0;

        int hour = timeInput.Time.Hours;
        int minute = timeInput.Time.Minutes;

        string? conflict = state.ScheduleManager.CheckConflict(action, "weekly", new ConflictTimeInfo
        {
            Weekday = weekday,
            Hour = hour,
            Minute = minute
        });

        if (conflict != null)
        {
            await ShowErrorAsync(xamlRoot, Strings.Format("Schedule_Conflict", conflict));
            return;
        }

        state.ScheduleManager.AddWeekly(action, weekday, hour, minute);
        state.AddActivityLog(Strings.Format("Schedule_AddedWeekly", AppConstants.WeekdaysJp[weekday], hour, minute));
        await state.SyncDevicesAsync(silent: true);
        refresh();
    }

    public static async Task AddOnetimeAsync(
        XamlRoot xamlRoot,
        AppState state,
        string action,
        CompactDateTimeInput dateTimeInput,
        Action refresh)
    {
        var target = dateTimeInput.DateTime;
        if (target <= System.DateTime.Now)
        {
            await ShowErrorAsync(xamlRoot, Strings.Get("Schedule_PastTime"));
            return;
        }

        string dtStr = target.ToString("yyyy-MM-dd HH:mm");
        string? conflict = state.ScheduleManager.CheckConflict(action, "onetime", new ConflictTimeInfo
        {
            TargetDateTime = target
        });

        if (conflict != null)
        {
            await ShowErrorAsync(xamlRoot, Strings.Format("Schedule_Conflict", conflict));
            return;
        }

        state.ScheduleManager.AddOnetime(action, dtStr);
        state.AddActivityLog(Strings.Format("Schedule_AddedOnetime", dtStr));
        await state.SyncDevicesAsync(silent: true);
        refresh();
    }

    public static async Task QuickHoursLaterAsync(AppState state, string action, int hours, Action refresh)
    {
        state.ScheduleManager.AddOnetimeHoursLater(action, hours);
        state.AddActivityLog(Strings.Format("Schedule_QuickAdded", hours));
        await state.SyncDevicesAsync(silent: true);
        refresh();
    }

    public static async Task CancelNextAsync(AppState state, string action, Action refresh)
    {
        var next = state.ScheduleManager.GetNextEventForAction(action);
        if (next == null)
            return;

        PowerStateHelper.AbortPendingShutdown();
        state.ScheduleManager.ClearPendingAction();

        if (next.ScheduleType == "onetime" && next.ScheduleId != null)
        {
            state.ScheduleManager.RemoveOnetime(next.ScheduleId);
        }
        else
        {
            state.ScheduleManager.SkipDateTime(next.DateTime.ToString("yyyy-MM-dd HH:mm"));
        }

        state.AddActivityLog(Strings.Get("Schedule_Cancelled"));
        await state.SyncDevicesAsync(silent: true);
        refresh();
    }

    public static void BindWeeklyList(ListView list, ScheduleManager manager, string action)
    {
        list.ItemsSource = manager.Data.WeeklySchedules
            .Where(s => s.Action == action)
            .Select(s => $"{AppConstants.WeekdaysJp[s.Weekday]} {s.Hour:00}:{s.Minute:00}")
            .ToList();
    }

    public static void BindOnetimeList(ListView list, ScheduleManager manager, string action)
    {
        list.ItemsSource = manager.Data.OnetimeSchedules
            .Where(s => s.Action == action && !s.Executed)
            .Select(s => s.Datetime)
            .ToList();
    }

    private static async Task ShowErrorAsync(XamlRoot xamlRoot, string message)
    {
        var dialog = new ContentDialog
        {
            Title = Strings.Get("Error_Title"),
            Content = message,
            CloseButtonText = Strings.Get("Common_OK"),
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }
}
