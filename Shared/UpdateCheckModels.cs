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

    /// <summary>
    /// setup.exe の SHA256（16 進の小文字。GitHub の asset の digest から取る）。
    /// GitHub が返さない古いリリースなどでは null（そのときは照合しない）。
    /// </summary>
    public string? AssetSha256 { get; set; }
}
