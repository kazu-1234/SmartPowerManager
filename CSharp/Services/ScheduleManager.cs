using Newtonsoft.Json;
using SmartPowerManager.Models;

namespace SmartPowerManager.Services;

public sealed class ScheduleManager
{
    private readonly object _lock = new();
    private readonly string _configPath;

    public ScheduleData Data { get; private set; } = new();
    public bool LoadFailed { get; private set; }
    public PendingActionRequest? PendingAction { get; private set; }

    public ScheduleManager(string? configPath = null)
    {
        _configPath = configPath ?? AppPaths.SchedulesFilePath;
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_configPath))
            return;

        lock (_lock)
        {
            try
            {
                string json = File.ReadAllText(_configPath);
                var data = JsonConvert.DeserializeObject<ScheduleData>(json);
                if (data == null)
                {
                    LoadFailed = true;
                    return;
                }

                NormalizeLoadedData(data);
                Data = data;
                LoadFailed = false;
            }
            catch
            {
                LoadFailed = true;
            }
        }
    }

    public void Save()
    {
        if (LoadFailed)
            return;

        lock (_lock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonConvert.SerializeObject(Data, Formatting.Indented);
                File.WriteAllText(_configPath, json);
            }
            catch
            {
                // 保存失敗は呼び出し元でログ
            }
        }
    }

    public string? CheckConflict(string actionType, string scheduleType, ConflictTimeInfo timeInfo)
    {
        string otherAction = actionType == AppConstants.ActionShutdown
            ? AppConstants.ActionRestart
            : AppConstants.ActionShutdown;
        string otherLabel = otherAction == AppConstants.ActionRestart ? "再起動" : "シャットダウン";

        if (scheduleType == "daily")
        {
            var otherDaily = GetDaily(otherAction);
            if (otherDaily.Enabled &&
                otherDaily.Hour == timeInfo.Hour &&
                otherDaily.Minute == timeInfo.Minute)
            {
                return $"毎日 ({otherDaily.Hour:00}:{otherDaily.Minute:00}) の{otherLabel}";
            }
        }
        else if (scheduleType == "weekly")
        {
            foreach (var s in Data.WeeklySchedules.Where(s => s.Action == otherAction))
            {
                if (s.Weekday == timeInfo.Weekday &&
                    s.Hour == timeInfo.Hour &&
                    s.Minute == timeInfo.Minute)
                {
                    return $"毎週 {AppConstants.WeekdaysJp[s.Weekday]} {s.Hour:00}:{s.Minute:00} の{otherLabel}";
                }
            }

            var otherDaily = GetDaily(otherAction);
            if (otherDaily.Enabled &&
                otherDaily.Hour == timeInfo.Hour &&
                otherDaily.Minute == timeInfo.Minute)
            {
                return $"毎日 ({otherDaily.Hour:00}:{otherDaily.Minute:00}) の{otherLabel}";
            }
        }
        else if (scheduleType == "onetime" && timeInfo.TargetDateTime.HasValue)
        {
            var targetDt = timeInfo.TargetDateTime.Value;
            foreach (var s in Data.OnetimeSchedules.Where(s => s.Action == otherAction && !s.Executed))
            {
                if (DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                        System.Globalization.DateTimeStyles.None, out var sDt) &&
                    sDt == targetDt)
                {
                    return $"一回限り {s.Datetime} の{otherLabel}";
                }
            }

            int targetWeekday = ((int)targetDt.DayOfWeek + 6) % 7;
            foreach (var s in Data.WeeklySchedules.Where(s => s.Action == otherAction))
            {
                if (s.Weekday == targetWeekday &&
                    s.Hour == targetDt.Hour &&
                    s.Minute == targetDt.Minute)
                {
                    return $"毎週 {AppConstants.WeekdaysJp[s.Weekday]} {s.Hour:00}:{s.Minute:00} の{otherLabel}";
                }
            }

            var otherDaily = GetDaily(otherAction);
            if (otherDaily.Enabled &&
                otherDaily.Hour == targetDt.Hour &&
                otherDaily.Minute == targetDt.Minute)
            {
                return $"毎日 ({otherDaily.Hour:00}:{otherDaily.Minute:00}) の{otherLabel}";
            }
        }

        return null;
    }

    public DailyActionSetting GetDaily(string action) =>
        Data.Daily.TryGetValue(action, out var setting)
            ? setting
            : new DailyActionSetting();

    public void SetDaily(string action, bool enabled, int hour, int minute)
    {
        Data.Daily[action] = new DailyActionSetting
        {
            Enabled = enabled,
            Hour = hour,
            Minute = minute
        };

        // 毎日のオン／オフや時刻変更では、過去の「1回削除／キャンセル」を引き継がない
        ClearSkippedTimes(hour, minute);
        Save();
    }

    public string AddWeekly(string action, int weekday, int hour, int minute)
    {
        var schedule = new WeeklySchedule
        {
            Action = action,
            Weekday = weekday,
            Hour = hour,
            Minute = minute
        };
        Data.WeeklySchedules.Add(schedule);
        // 同じ時刻の過去スキップは引き継がない
        ClearSkippedTimes(hour, minute);
        Save();
        return schedule.Id;
    }

    public void RemoveWeekly(string scheduleId)
    {
        var existing = Data.WeeklySchedules.FirstOrDefault(s => s.Id == scheduleId);
        if (existing != null)
            ClearSkippedTimes(existing.Hour, existing.Minute);

        Data.WeeklySchedules.RemoveAll(s => s.Id == scheduleId);
        Save();
    }

    public string AddOnetime(string action, string dtStr, string source = "manual")
    {
        var schedule = new OnetimeSchedule
        {
            Action = action,
            Datetime = dtStr,
            Source = source
        };
        Data.OnetimeSchedules.Add(schedule);
        Save();
        return schedule.Id;
    }

    public string AddOnetimeHoursLater(string action, int hours)
    {
        var targetTime = DateTime.Now.AddHours(hours);
        return AddOnetime(action, targetTime.ToString("yyyy-MM-dd HH:mm"), "quick");
    }

    public void RemoveOnetime(string scheduleId)
    {
        Data.OnetimeSchedules.RemoveAll(s => s.Id == scheduleId);
        Save();
    }

    public void ClearExecutedOnetime(string action)
    {
        Data.OnetimeSchedules.RemoveAll(s => s.Action == action && s.Executed);
        Save();
    }

    public void SkipDateTime(string dtStr)
    {
        if (!Data.SkippedDates.Contains(dtStr))
        {
            Data.SkippedDates.Add(dtStr);
            Save();
        }
    }

    /// <summary>
    /// 指定時刻（HH:mm）に紐づくスキップをすべて解除する。
    /// 毎日スケジュールの再有効化時などに、過去の1回削除を引き継がないため。
    /// </summary>
    public bool ClearSkippedTimes(int hour, int minute)
    {
        string suffix = $" {hour:00}:{minute:00}";
        return Data.SkippedDates.RemoveAll(d =>
            d.Length >= suffix.Length && d.EndsWith(suffix, StringComparison.Ordinal)) > 0;
    }

    /// <summary>スキップ一覧を全クリアする。</summary>
    public void ClearAllSkippedDates()
    {
        if (Data.SkippedDates.Count == 0)
            return;

        Data.SkippedDates.Clear();
        Save();
    }

    public NextEventInfo? GetNextEventForAction(string action)
    {
        var all = GetAllNextEvents();
        return all.FirstOrDefault(e => e.Action == action);
    }

    public NextEventInfo? GetNextEvent()
    {
        var all = GetAllNextEvents();
        return all.FirstOrDefault();
    }

    public List<NextEventInfo> GetAllNextEvents()
    {
        var now = DateTime.Now;
        var candidates = new List<NextEventInfo>();

        foreach (var s in Data.OnetimeSchedules.Where(s => !s.Executed))
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                continue;

            if (dt <= now || Data.SkippedDates.Contains(s.Datetime))
                continue;

            candidates.Add(new NextEventInfo
            {
                DateTime = dt,
                ScheduleType = "onetime",
                Action = s.Action,
                ScheduleId = s.Id
            });
        }

        foreach (var s in Data.WeeklySchedules)
        {
            var next = GetNextWeeklyOccurrence(now, s.Weekday, s.Hour, s.Minute, Data.SkippedDates);
            if (next.HasValue)
            {
                candidates.Add(new NextEventInfo
                {
                    DateTime = next.Value,
                    ScheduleType = "weekly",
                    Action = s.Action,
                    ScheduleId = s.Id
                });
            }
        }

        foreach (var action in new[] { AppConstants.ActionShutdown, AppConstants.ActionRestart })
        {
            var setting = GetDaily(action);
            if (!setting.Enabled)
                continue;

            var next = GetNextDailyOccurrence(now, setting.Hour, setting.Minute, Data.SkippedDates);
            if (next.HasValue)
            {
                candidates.Add(new NextEventInfo
                {
                    DateTime = next.Value,
                    ScheduleType = "daily",
                    Action = action,
                    ScheduleId = null
                });
            }
        }

        return candidates.OrderBy(c => c.DateTime).ToList();
    }

    /// <summary>
    /// 指定アクションの監視オフ時などに、すでに時刻を過ぎた予定を実行せず除去する。
    /// 一回限りは削除、毎日／毎週の当該回はスキップ登録。
    /// </summary>
    public bool DiscardElapsedSchedules(string action, Action<string>? logCallback = null)
    {
        var now = DateTime.Now;
        int currentWeekday = ((int)now.DayOfWeek + 6) % 7;
        bool changed = false;
        string label = action == AppConstants.ActionShutdown ? "シャットダウン" : "再起動";

        foreach (var s in Data.OnetimeSchedules.Where(s => s.Action == action && !s.Executed).ToList())
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var scheduledDt))
                continue;

            if (scheduledDt > now)
                continue;

            Data.OnetimeSchedules.Remove(s);
            logCallback?.Invoke($"監視オフのため予定を削除: {label} 一回限り ({s.Datetime})");
            changed = true;
        }

        foreach (var s in Data.WeeklySchedules.Where(s => s.Action == action))
        {
            if (currentWeekday != s.Weekday || now.Hour != s.Hour || now.Minute != s.Minute)
                continue;

            string dtStr = now.ToString("yyyy-MM-dd HH:mm");
            if (Data.SkippedDates.Contains(dtStr))
                continue;

            Data.SkippedDates.Add(dtStr);
            logCallback?.Invoke($"監視オフのため予定をスキップ: {label} 毎週 ({dtStr})");
            changed = true;
        }

        if (Data.Daily.TryGetValue(action, out var daily) && daily.Enabled
            && now.Hour == daily.Hour && now.Minute == daily.Minute)
        {
            string dtStr = now.ToString("yyyy-MM-dd HH:mm");
            if (!Data.SkippedDates.Contains(dtStr))
            {
                Data.SkippedDates.Add(dtStr);
                logCallback?.Invoke($"監視オフのため予定をスキップ: {label} 毎日 ({dtStr})");
                changed = true;
            }
        }

        if (changed)
            Save();

        return changed;
    }

    /// <summary>
    /// 発火枠（60秒）を過ぎた一回限りを、実行せず削除する（監視オン／オフ問わず）。
    /// </summary>
    public bool DiscardStaleOnetimes(Action<string>? logCallback = null)
    {
        var now = DateTime.Now;
        bool changed = false;

        foreach (var s in Data.OnetimeSchedules.Where(s => !s.Executed).ToList())
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var scheduledDt))
                continue;

            if ((now - scheduledDt).TotalSeconds < 60)
                continue;

            Data.OnetimeSchedules.Remove(s);
            logCallback?.Invoke($"過ぎた予定を削除（未実行）: 一回限り ({s.Datetime})");
            changed = true;
        }

        foreach (var s in Data.PicoSettings.StartupOnetime.ToList())
        {
            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var scheduledDt))
                continue;

            if ((now - scheduledDt).TotalSeconds < 60)
                continue;

            Data.PicoSettings.StartupOnetime.Remove(s);
            logCallback?.Invoke($"過ぎた予定を削除（未実行）: 起動 一回限り ({s.Datetime})");
            changed = true;
        }

        if (changed)
            Save();

        return changed;
    }

    public bool CheckAndExecute(Func<string, bool> isMonitoring, Action<string>? logCallback = null)
    {
        var now = DateTime.Now;
        int currentWeekday = ((int)now.DayOfWeek + 6) % 7;

        foreach (var s in Data.OnetimeSchedules.Where(s => !s.Executed))
        {
            if (!isMonitoring(s.Action))
                continue;

            if (!DateTime.TryParseExact(s.Datetime, "yyyy-MM-dd HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var scheduledDt))
                continue;

            // 過ぎた予定は実行しない（未来側の早発も不可）。予定時刻～60秒未満のみ
            double elapsed = (now - scheduledDt).TotalSeconds;
            if (elapsed < 0 || elapsed >= 60)
                continue;

            s.Executed = true;
            Save();
            QueueAction(s.Action, $"一回限り ({s.Datetime})", logCallback);
            return true;
        }

        foreach (var s in Data.WeeklySchedules)
        {
            if (!isMonitoring(s.Action))
                continue;

            if (currentWeekday != s.Weekday || now.Hour != s.Hour || now.Minute != s.Minute)
                continue;

            string dtStr = now.ToString("yyyy-MM-dd HH:mm");
            if (Data.SkippedDates.Contains(dtStr))
            {
                logCallback?.Invoke($"スキップされたスケジュール: 毎週 {dtStr}");
                return false;
            }

            QueueAction(s.Action,
                $"毎週 ({AppConstants.WeekdaysJp[s.Weekday]} {s.Hour:00}:{s.Minute:00})",
                logCallback);
            return true;
        }

        foreach (var pair in Data.Daily)
        {
            if (!pair.Value.Enabled || !isMonitoring(pair.Key))
                continue;

            if (now.Hour != pair.Value.Hour || now.Minute != pair.Value.Minute)
                continue;

            string dtStr = now.ToString("yyyy-MM-dd HH:mm");
            if (Data.SkippedDates.Contains(dtStr))
            {
                logCallback?.Invoke($"スキップされたスケジュール: 毎日 {dtStr}");
                return false;
            }

            QueueAction(pair.Key,
                $"毎日 ({pair.Value.Hour:00}:{pair.Value.Minute:00})",
                logCallback);
            return true;
        }

        return false;
    }

    public void ClearPendingAction() => PendingAction = null;

    public string AddStartupWeekly(int weekday, int hour, int minute)
    {
        var schedule = new StartupWeeklySchedule
        {
            Weekday = weekday,
            Hour = hour,
            Minute = minute
        };
        Data.PicoSettings.StartupWeekly.Add(schedule);
        ClearSkippedTimes(hour, minute);
        Save();
        return schedule.Id;
    }

    public void RemoveStartupWeekly(string scheduleId)
    {
        var existing = Data.PicoSettings.StartupWeekly.FirstOrDefault(s => s.Id == scheduleId);
        if (existing != null)
            ClearSkippedTimes(existing.Hour, existing.Minute);

        Data.PicoSettings.StartupWeekly.RemoveAll(s => s.Id == scheduleId);
        Save();
    }

    public void SetStartupDaily(bool enabled, int hour, int minute)
    {
        Data.PicoSettings.StartupDaily = new StartupDailySetting
        {
            Enabled = enabled,
            Hour = hour,
            Minute = minute
        };
        ClearSkippedTimes(hour, minute);
        Save();
    }

    public string AddStartupOnetime(string dtStr, string source = "manual")
    {
        var schedule = new StartupOnetimeSchedule
        {
            Datetime = dtStr,
            Source = source
        };
        Data.PicoSettings.StartupOnetime.Add(schedule);
        Save();
        return schedule.Id;
    }

    public string AddStartupOnetimeHoursLater(int hours)
    {
        var targetTime = DateTime.Now.AddHours(hours);
        return AddStartupOnetime(targetTime.ToString("yyyy-MM-dd HH:mm"), "quick");
    }

    public void RemoveStartupOnetime(string scheduleId)
    {
        Data.PicoSettings.StartupOnetime.RemoveAll(s => s.Id == scheduleId);
        Save();
    }

    public void ClearAllStartupSchedules()
    {
        Data.PicoSettings.StartupDaily.Enabled = false;
        Data.PicoSettings.StartupWeekly.Clear();
        Data.PicoSettings.StartupOnetime.Clear();
        Save();
    }

    public void ApplyPicoResponse(Newtonsoft.Json.Linq.JObject data)
    {
        if (data["daily"] is Newtonsoft.Json.Linq.JObject daily)
        {
            Data.PicoSettings.StartupDaily = new StartupDailySetting
            {
                Enabled = daily.Value<bool?>("enabled") ?? false,
                Hour = daily.Value<int?>("hour") ?? 0,
                Minute = daily.Value<int?>("minute") ?? 0
            };
        }

        if (data["weekly"] is Newtonsoft.Json.Linq.JArray weekly)
        {
            Data.PicoSettings.StartupWeekly.Clear();
            foreach (var item in weekly.OfType<Newtonsoft.Json.Linq.JObject>())
            {
                Data.PicoSettings.StartupWeekly.Add(new StartupWeeklySchedule
                {
                    Weekday = item.Value<int?>("weekday") ?? 0,
                    Hour = item.Value<int?>("hour") ?? 0,
                    Minute = item.Value<int?>("minute") ?? 0
                });
            }
        }

        if (data["onetime"] is Newtonsoft.Json.Linq.JArray onetime)
        {
            Data.PicoSettings.StartupOnetime.Clear();
            foreach (var item in onetime.OfType<Newtonsoft.Json.Linq.JObject>())
            {
                int year = item.Value<int?>("year") ?? 0;
                int month = item.Value<int?>("month") ?? 0;
                int day = item.Value<int?>("day") ?? 0;
                int hour = item.Value<int?>("hour") ?? 0;
                int minute = item.Value<int?>("minute") ?? 0;
                string src = item.Value<string>("source") ?? "manual";
                string dtStr = $"{year}-{month:00}-{day:00} {hour:00}:{minute:00}";
                Data.PicoSettings.StartupOnetime.Add(new StartupOnetimeSchedule
                {
                    Datetime = dtStr,
                    Source = src
                });
            }
        }

        Save();
    }

    public static DateTime? GetNextWeeklyOccurrence(DateTime now, int weekday, int hour, int minute, IList<string>? skippedDates = null)
    {
        var targetTime = now.Date.AddHours(hour).AddMinutes(minute);
        int daysAhead = weekday - ((int)now.DayOfWeek + 6) % 7;
        if (daysAhead < 0 || (daysAhead == 0 && targetTime <= now))
            daysAhead += 7;

        var next = targetTime.AddDays(daysAhead);
        skippedDates ??= [];

        for (int i = 0; i < 5; i++)
        {
            string dtStr = next.ToString("yyyy-MM-dd HH:mm");
            if (!skippedDates.Contains(dtStr))
                return next;
            next = next.AddDays(7);
        }

        return null;
    }

    public static DateTime? GetNextDailyOccurrence(DateTime now, int hour, int minute, IList<string>? skippedDates = null)
    {
        var targetTime = now.Date.AddHours(hour).AddMinutes(minute);
        if (targetTime <= now)
            targetTime = targetTime.AddDays(1);

        skippedDates ??= [];

        for (int i = 0; i < 5; i++)
        {
            string dtStr = targetTime.ToString("yyyy-MM-dd HH:mm");
            if (!skippedDates.Contains(dtStr))
                return targetTime;
            targetTime = targetTime.AddDays(1);
        }

        return null;
    }

    private void QueueAction(string action, string triggerLabel, Action<string>? logCallback)
    {
        string label = action == AppConstants.ActionShutdown ? "シャットダウン" : "再起動";
        logCallback?.Invoke($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {label}予定: {triggerLabel}");

        PendingAction = new PendingActionRequest
        {
            Action = action,
            TriggerLabel = triggerLabel
        };
    }

    private static void NormalizeLoadedData(ScheduleData data)
    {
        if (data.Daily.Count == 0)
        {
            data.Daily[AppConstants.ActionShutdown] = new DailyActionSetting { Hour = 23 };
            data.Daily[AppConstants.ActionRestart] = new DailyActionSetting { Hour = 23 };
        }

        foreach (var item in data.WeeklySchedules.Where(s => string.IsNullOrWhiteSpace(s.Action)))
            item.Action = AppConstants.ActionShutdown;

        foreach (var item in data.OnetimeSchedules.Where(s => string.IsNullOrWhiteSpace(s.Action)))
            item.Action = AppConstants.ActionShutdown;

        data.PicoSettings ??= new PicoSettings();
        data.PicoSettings.StartupDaily ??= new StartupDailySetting();
        data.PicoSettings.StartupWeekly ??= [];
        data.PicoSettings.StartupOnetime ??= [];
        data.SkippedDates ??= [];
    }
}

public sealed class ConflictTimeInfo
{
    public int Hour { get; init; }
    public int Minute { get; init; }
    public int Weekday { get; init; }
    public DateTime? TargetDateTime { get; init; }
}
