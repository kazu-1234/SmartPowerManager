using System.Text;
using SmartPowerManager.Models;

namespace SmartPowerManager.Services;

public static class AutoWolCalculator
{
    public sealed class WolTrigger
    {
        public string Type { get; init; } = string.Empty;
        public int Hour { get; init; }
        public int Minute { get; init; }
        public int Weekday { get; init; }
        public string? Datetime { get; init; }
    }

    public static List<WolTrigger> BuildWolTriggers(ScheduleData data, bool includeStartupSchedules)
    {
        var triggers = new List<WolTrigger>();

        if (includeStartupSchedules)
        {
            var daily = data.PicoSettings.StartupDaily;
            if (daily.Enabled)
            {
                triggers.Add(new WolTrigger
                {
                    Type = "daily",
                    Hour = daily.Hour,
                    Minute = daily.Minute
                });
            }

            foreach (var s in data.PicoSettings.StartupWeekly)
            {
                triggers.Add(new WolTrigger
                {
                    Type = "weekly",
                    Weekday = s.Weekday,
                    Hour = s.Hour,
                    Minute = s.Minute
                });
            }

            foreach (var s in data.PicoSettings.StartupOnetime)
            {
                if (DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                        System.Globalization.DateTimeStyles.None, out _))
                {
                    triggers.Add(new WolTrigger
                    {
                        Type = "onetime",
                        Datetime = s.Datetime
                    });
                }
            }
        }

        foreach (string action in new[] { AppConstants.ActionShutdown, AppConstants.ActionRestart })
        {
            AddAutoWolForAction(data, action, triggers);
        }

        return triggers;
    }

    public static (string Weekly, string Onetime) BuildPicoAutoWolStrings(ScheduleData data)
    {
        var weekly = new StringBuilder();
        var onetime = new StringBuilder();

        foreach (string action in new[] { AppConstants.ActionShutdown, AppConstants.ActionRestart })
        {
            AppendPicoAutoWolForAction(data, action, weekly, onetime);
        }

        return (weekly.ToString(), onetime.ToString());
    }

    private static void AddAutoWolForAction(ScheduleData data, string action, List<WolTrigger> triggers)
    {
        if (data.Daily.TryGetValue(action, out var daily) && daily.Enabled)
        {
            var dtWol = new DateTime(2000, 1, 1, daily.Hour, daily.Minute, 0).AddMinutes(-3);
            triggers.Add(new WolTrigger
            {
                Type = "daily",
                Hour = dtWol.Hour,
                Minute = dtWol.Minute
            });
        }

        foreach (var s in data.WeeklySchedules.Where(s => s.Action == action))
        {
            int wd = s.Weekday;
            var dtWol = new DateTime(2000, 1, 1, s.Hour, s.Minute, 0).AddMinutes(-3);
            if (s.Hour == 0 && s.Minute < 3)
                wd = (wd - 1 + 7) % 7;

            triggers.Add(new WolTrigger
            {
                Type = "weekly",
                Weekday = wd,
                Hour = dtWol.Hour,
                Minute = dtWol.Minute
            });
        }

        foreach (var s in data.OnetimeSchedules.Where(s => s.Action == action && !s.Executed))
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                continue;

            var wakeDt = dt.AddMinutes(-3);
            if (wakeDt > DateTime.Now)
            {
                triggers.Add(new WolTrigger
                {
                    Type = "onetime",
                    Datetime = wakeDt.ToString("yyyy-MM-dd HH:mm")
                });
            }
        }
    }

    private static void AppendPicoAutoWolForAction(
        ScheduleData data,
        string action,
        StringBuilder weekly,
        StringBuilder onetime)
    {
        if (data.Daily.TryGetValue(action, out var daily) && daily.Enabled)
        {
            var dtBase = new DateTime(2000, 1, 1, daily.Hour, daily.Minute, 0).AddMinutes(-3);
            for (int wd = 0; wd < 7; wd++)
                weekly.Append($"{wd},{dtBase.Hour},{dtBase.Minute};");
        }

        foreach (var s in data.WeeklySchedules.Where(s => s.Action == action))
        {
            int wd = s.Weekday;
            if (s.Hour == 0 && s.Minute < 3)
                wd = (wd - 1 + 7) % 7;

            var dtBase = new DateTime(2000, 1, 1, s.Hour, s.Minute, 0).AddMinutes(-3);
            weekly.Append($"{wd},{dtBase.Hour},{dtBase.Minute};");
        }

        foreach (var s in data.OnetimeSchedules.Where(s => s.Action == action && !s.Executed))
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                continue;

            var wakeDt = dt.AddMinutes(-3);
            if (wakeDt > DateTime.Now)
                onetime.Append($"{wakeDt.Year},{wakeDt.Month},{wakeDt.Day},{wakeDt.Hour},{wakeDt.Minute},auto_wol;");
        }
    }
}
