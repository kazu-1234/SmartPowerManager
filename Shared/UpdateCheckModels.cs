namespace WinUiShared;

public enum UpdateCheckStatus
{
    NotConfigured,
    UpToDate,
    UpdateAvailable,
    Error
}

public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? LatestVersion { get; set; }
    public string? ReleasePageUrl { get; set; }
    public string? DownloadUrl { get; set; }
    public string? AssetFileName { get; set; }
}
