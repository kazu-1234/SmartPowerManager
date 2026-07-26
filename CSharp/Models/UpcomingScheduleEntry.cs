namespace SmartPowerManager.Models;

public sealed class UpcomingScheduleEntry
{
    public required string Category { get; init; }
    public required string CategoryLabel { get; init; }
    public required string Description { get; init; }
    public string? DateTimeText { get; init; }
    public DateTime? SortKey { get; init; }
    public required string EntryKind { get; init; }
    public string? Action { get; init; }
    public string? ScheduleId { get; init; }
    public string? ScheduleType { get; init; }

    /// <summary>毎日・毎週など繰り返し系で「全削除」を出すか。</summary>
    public bool SupportsDeleteAll { get; init; }
}
