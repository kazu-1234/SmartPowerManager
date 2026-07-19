using Newtonsoft.Json;

namespace SmartPowerManager.Models;

public class DailyActionSetting
{
    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("hour")]
    public int Hour { get; set; }

    [JsonProperty("minute")]
    public int Minute { get; set; }
}

public class WeeklySchedule
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("action")]
    public string Action { get; set; } = AppConstants.ActionShutdown;

    [JsonProperty("weekday")]
    public int Weekday { get; set; }

    [JsonProperty("hour")]
    public int Hour { get; set; }

    [JsonProperty("minute")]
    public int Minute { get; set; }
}

public class OnetimeSchedule
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("action")]
    public string Action { get; set; } = AppConstants.ActionShutdown;

    [JsonProperty("datetime")]
    public string Datetime { get; set; } = string.Empty;

    [JsonProperty("executed")]
    public bool Executed { get; set; }

    [JsonProperty("source")]
    public string Source { get; set; } = "manual";
}

public class StartupDailySetting
{
    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("hour")]
    public int Hour { get; set; } = 7;

    [JsonProperty("minute")]
    public int Minute { get; set; }
}

public class StartupWeeklySchedule
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("weekday")]
    public int Weekday { get; set; }

    [JsonProperty("hour")]
    public int Hour { get; set; }

    [JsonProperty("minute")]
    public int Minute { get; set; }

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;
}

public class StartupOnetimeSchedule
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("datetime")]
    public string Datetime { get; set; } = string.Empty;

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("source")]
    public string Source { get; set; } = "manual";
}

public class PicoSettings
{
    [JsonProperty("ip")]
    public string Ip { get; set; } = AppConstants.DefaultPicoIp;

    [JsonProperty("target_mac")]
    public string TargetMac { get; set; } = string.Empty;

    [JsonProperty("gas_url")]
    public string GasUrl { get; set; } = string.Empty;

    [JsonProperty("gas_target")]
    public string GasTarget { get; set; } = AppConstants.GasTargetDesktop;

    [JsonProperty("startup_daily")]
    public StartupDailySetting StartupDaily { get; set; } = new();

    [JsonProperty("startup_weekly")]
    public List<StartupWeeklySchedule> StartupWeekly { get; set; } = [];

    [JsonProperty("startup_onetime")]
    public List<StartupOnetimeSchedule> StartupOnetime { get; set; } = [];
}

public class ScheduleData
{
    [JsonProperty("daily")]
    public Dictionary<string, DailyActionSetting> Daily { get; set; } = new()
    {
        [AppConstants.ActionShutdown] = new DailyActionSetting { Hour = 23 },
        [AppConstants.ActionRestart] = new DailyActionSetting { Hour = 23 }
    };

    [JsonProperty("weekly_schedules")]
    public List<WeeklySchedule> WeeklySchedules { get; set; } = [];

    [JsonProperty("onetime")]
    public List<OnetimeSchedule> OnetimeSchedules { get; set; } = [];

    [JsonProperty("pico_settings")]
    public PicoSettings PicoSettings { get; set; } = new();

    [JsonProperty("debug_mode")]
    public bool DebugMode { get; set; }

    [JsonProperty("disclaimer_accepted")]
    public bool DisclaimerAccepted { get; set; }

    [JsonProperty("skipped_dates")]
    public List<string> SkippedDates { get; set; } = [];
}

public sealed class PendingActionRequest
{
    public required string Action { get; init; }
    public required string TriggerLabel { get; init; }
}

public sealed class NextEventInfo
{
    public DateTime DateTime { get; init; }
    public string ScheduleType { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? ScheduleId { get; init; }
}
