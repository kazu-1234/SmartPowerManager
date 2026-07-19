namespace SmartPowerManager;

public static class AppConstants
{
    public const string AppTitle = "SmartPowerManager";
    public const string ActionShutdown = "shutdown";
    public const string ActionRestart = "restart";
    public const string DefaultPicoIp = "192.168.10.x";
    public const string GasTargetDesktop = "デスクトップPC";
    public const string GasTargetServer = "サーバーPC";

    public static readonly string[] WeekdaysJp = ["月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日", "日曜日"];
    public static readonly string[] WeekdaysShort = ["月", "火", "水", "木", "金", "土", "日"];
    public static readonly int[] HoursLaterOptions = [1, 3, 6, 9, 12];

    public const string GitHubUser = "kazu-1234";
    public const string GitHubRepo = "SmartPowerManager";
    public const string GitHubApiUrl = "https://api.github.com/repos/kazu-1234/SmartPowerManager/releases/latest";

    public const string PythonRegistryName = "SmartPowerManager";

    /// <summary>実行予定リストに見せる行数（今後1週間の日数）。</summary>
    public const int ScheduleListVisibleRows = 7;

    /// <summary>実行予定1行分の高さ（余白込み）。</summary>
    public const double ScheduleEntryRowHeight = 56;

    /// <summary>右カード見出しブロックの高さ。</summary>
    public const double SchedulePanelHeaderHeight = 28;

    /// <summary>カード上下パディング合計（左右は右余白多めでも高さは上下のみ）。</summary>
    public const double ScheduleCardPaddingVertical = 20;

    /// <summary>右カード見出しとリストの間の RowSpacing。</summary>
    public const double SchedulePanelRowSpacing = 8;

    /// <summary>左右カード共通の固定高さ（7件分の表示枠に合わせる）。</summary>
    public static double ScheduleCardHeight =>
        ScheduleCardPaddingVertical
        + SchedulePanelHeaderHeight
        + SchedulePanelRowSpacing
        + ScheduleListVisibleRows * ScheduleEntryRowHeight;
}
