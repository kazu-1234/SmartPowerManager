using Microsoft.UI.Xaml;
using WinUiShared;

namespace SmartPowerManager.Services;

/// <summary>更新確認のアプリ側入口。実体は WinUiShared.UpdateFlow（SPM 正本）。</summary>
public static class UpdateFlowService
{
    public static string FormatLastUpdateCheckDisplay(DateTime? lastCheckUtc) =>
        UpdateFlow.FormatLastUpdateCheckDisplay(
            lastCheckUtc,
            Strings.Get,
            (key, args) => Strings.Format(key, args));

    public static Task<UpdateCheckResult> CheckAndRecordAsync(Settings settings) =>
        UpdateFlow.CheckAndRecordAsync(settings, UpdateChecker.CheckForUpdateAsync);

    public static Task TryStartupCheckAsync(Window window, Settings settings) =>
        UpdateFlow.TryStartupCheckAsync(
            window,
            settings,
            Strings.Get,
            (key, args) => Strings.Format(key, args),
            UpdateChecker.CheckForUpdateAsync,
            (url, name) => UpdateInstallerService.DownloadAndInstallAsync(url, name));
}
