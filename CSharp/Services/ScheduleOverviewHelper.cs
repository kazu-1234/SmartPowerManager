using SmartPowerManager.Models;

namespace SmartPowerManager.Services;

public static class ScheduleOverviewHelper
{
    private static readonly TimeSpan UpcomingWindow = TimeSpan.FromDays(7);

    public static List<UpcomingScheduleEntry> BuildUpcomingEntries(
        ScheduleManager manager,
        string? categoryFilter = null,
        Func<string, bool>? isActionVisible = null)
    {
        var data = manager.Data;
        var now = DateTime.Now;
        var weekEnd = now.Add(UpcomingWindow);
        var entries = new List<UpcomingScheduleEntry>();

        bool ShowPower(string action) =>
            (categoryFilter is null || categoryFilter == action)
            && (isActionVisible?.Invoke(action) ?? true);

        if (ShowPower(AppConstants.ActionShutdown))
            AddPowerEvents(manager, data, now, weekEnd, AppConstants.ActionShutdown, "シャットダウン", entries);

        if (ShowPower(AppConstants.ActionRestart))
            AddPowerEvents(manager, data, now, weekEnd, AppConstants.ActionRestart, "再起動", entries);

        if (categoryFilter is null or "wake")
            AddWakeEvents(manager, data, now, weekEnd, entries);

        return entries
            .Where(e => e.SortKey.HasValue && e.SortKey.Value > now && e.SortKey.Value <= weekEnd)
            .OrderBy(e => e.SortKey)
            .ToList();
    }

    private static void AddPowerEvents(
        ScheduleManager manager,
        ScheduleData data,
        DateTime now,
        DateTime weekEnd,
        string action,
        string label,
        List<UpcomingScheduleEntry> entries)
    {
        foreach (var s in data.OnetimeSchedules.Where(s => s.Action == action && !s.Executed))
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                continue;

            if (dt <= now || dt > weekEnd || data.SkippedDates.Contains(s.Datetime))
                continue;

            entries.Add(CreateEntry(action, label, $"一回限り", dt, "onetime", s.Id, "onetime", supportsDeleteAll: false));
        }

        foreach (var s in data.WeeklySchedules.Where(s => s.Action == action))
        {
            string desc = $"毎週 {AppConstants.WeekdaysJp[s.Weekday]} {s.Hour:00}:{s.Minute:00}";
            foreach (var dt in EnumerateWeeklyOccurrences(now, weekEnd, s.Weekday, s.Hour, s.Minute, data.SkippedDates))
            {
                entries.Add(CreateEntry(action, label, desc, dt, "weekly", s.Id, "weekly", supportsDeleteAll: true));
            }
        }

        var daily = manager.GetDaily(action);
        if (daily.Enabled)
        {
            string desc = $"毎日 {daily.Hour:00}:{daily.Minute:00}";
            foreach (var dt in EnumerateDailyOccurrences(now, weekEnd, daily.Hour, daily.Minute, data.SkippedDates))
            {
                entries.Add(CreateEntry(action, label, desc, dt, "daily", null, "daily", supportsDeleteAll: true));
            }
        }
    }

    private static void AddWakeEvents(
        ScheduleManager manager,
        ScheduleData data,
        DateTime now,
        DateTime weekEnd,
        List<UpcomingScheduleEntry> entries)
    {
        var pico = data.PicoSettings;

        if (pico.StartupDaily.Enabled)
        {
            string desc = $"毎日 {pico.StartupDaily.Hour:00}:{pico.StartupDaily.Minute:00}";
            foreach (var dt in EnumerateDailyOccurrences(now, weekEnd, pico.StartupDaily.Hour, pico.StartupDaily.Minute, data.SkippedDates))
            {
                entries.Add(CreateEntry("wake", "起動", desc, dt, "wake_daily", null, "daily", supportsDeleteAll: true));
            }
        }

        foreach (var s in pico.StartupWeekly)
        {
            string desc = $"毎週 {AppConstants.WeekdaysJp[s.Weekday]} {s.Hour:00}:{s.Minute:00}";
            foreach (var dt in EnumerateWeeklyOccurrences(now, weekEnd, s.Weekday, s.Hour, s.Minute, data.SkippedDates))
            {
                entries.Add(CreateEntry("wake", "起動", desc, dt, "wake_weekly", s.Id, "weekly", supportsDeleteAll: true));
            }
        }

        foreach (var s in pico.StartupOnetime)
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                continue;

            if (dt <= now || dt > weekEnd)
                continue;

            entries.Add(CreateEntry("wake", "起動", $"一回限り", dt, "wake_onetime", s.Id, "onetime", supportsDeleteAll: false));
        }
    }

    private static IEnumerable<DateTime> EnumerateDailyOccurrences(
        DateTime now,
        DateTime weekEnd,
        int hour,
        int minute,
        IList<string> skippedDates)
    {
        var cursor = now.Date.AddHours(hour).AddMinutes(minute);
        if (cursor <= now)
            cursor = cursor.AddDays(1);

        while (cursor <= weekEnd)
        {
            string dtStr = cursor.ToString("yyyy-MM-dd HH:mm");
            if (!skippedDates.Contains(dtStr))
                yield return cursor;
            cursor = cursor.AddDays(1);
        }
    }

    private static IEnumerable<DateTime> EnumerateWeeklyOccurrences(
        DateTime now,
        DateTime weekEnd,
        int weekday,
        int hour,
        int minute,
        IList<string> skippedDates)
    {
        var cursor = ScheduleManager.GetNextWeeklyOccurrence(now, weekday, hour, minute, skippedDates);
        while (cursor.HasValue && cursor.Value <= weekEnd)
        {
            yield return cursor.Value;
            // 次の週へ（同じ曜日の次発生）
            var after = cursor.Value.AddMinutes(1);
            cursor = ScheduleManager.GetNextWeeklyOccurrence(after, weekday, hour, minute, skippedDates);
        }
    }

    private static UpcomingScheduleEntry CreateEntry(
        string category,
        string categoryLabel,
        string description,
        DateTime sortKey,
        string entryKind,
        string? scheduleId,
        string scheduleType,
        bool supportsDeleteAll)
    {
        return new UpcomingScheduleEntry
        {
            Category = category,
            CategoryLabel = categoryLabel,
            Description = description,
            DateTimeText = sortKey.ToString("yyyy-MM-dd HH:mm"),
            SortKey = sortKey,
            EntryKind = entryKind,
            Action = category is "wake" ? null : category,
            ScheduleId = scheduleId,
            ScheduleType = scheduleType,
            SupportsDeleteAll = supportsDeleteAll
        };
    }
}
