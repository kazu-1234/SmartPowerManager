using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace WinUiShared;

/// <summary>
/// 手動・起動時の更新確認フロー（SPM 正本）。各アプリは Strings / UpdateChecker を渡す。
/// </summary>
public static class UpdateFlow
{
    private static bool _startupCheckStarted;

    public static string FormatLastUpdateCheckDisplay(
        DateTime? lastCheckUtc,
        Func<string, string> getString,
        Func<string, object[], string> formatString)
    {
        if (lastCheckUtc == null)
            return getString("Info_LastUpdateCheck_Never");

        DateTime local = lastCheckUtc.Value.ToLocalTime();
        string dateFormat = getString("Info_LastUpdateCheck_DateFormat");
        string formatted = local.ToString(dateFormat, CultureInfo.CurrentUICulture);
        return formatString("Info_LastUpdateCheck_Format", new object[] { formatted });
    }

    public static void RecordLastUpdateCheck(IUpdateSettings settings)
    {
        settings.LastUpdateCheckUtc = DateTime.UtcNow;
        settings.Save();
    }

    public static async Task<UpdateCheckResult> CheckAndRecordAsync(
        IUpdateSettings settings,
        Func<Task<UpdateCheckResult>> checkAsync)
    {
        UpdateCheckResult result = await checkAsync();
        RecordLastUpdateCheck(settings);
        return result;
    }

    /// <summary>
    /// プロセス初回の MainWindow 表示後に 1 回だけ起動時チェックを行う。
    /// </summary>
    public static async Task TryStartupCheckAsync(
        Window window,
        IUpdateSettings settings,
        Func<string, string> getString,
        Func<string, object[], string> formatString,
        Func<Task<UpdateCheckResult>> checkAsync,
        Func<string, string, Task> installAsync)
    {
        if (_startupCheckStarted || !settings.AutoCheckUpdateOnStartup)
            return;

        _startupCheckStarted = true;

        try
        {
            await Task.Delay(2000);

            if (window.Content?.XamlRoot == null)
                return;

            UpdateCheckResult result = await CheckAndRecordAsync(settings, checkAsync);

            if (result.Status != UpdateCheckStatus.UpdateAvailable)
                return;

            var dialog = new ContentDialog
            {
                Title = getString("Update_StartupDialogTitle"),
                Content = formatString("Update_StartupDialogContent", new object[] { result.Message }),
                PrimaryButtonText = getString("Update_StartupDialogInstall"),
                CloseButtonText = getString("Update_StartupDialogLater"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = window.Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (!string.IsNullOrWhiteSpace(result.DownloadUrl)
                && !string.IsNullOrWhiteSpace(result.AssetFileName))
            {
                await installAsync(result.DownloadUrl, result.AssetFileName);
            }
            else if (!string.IsNullOrWhiteSpace(result.ReleasePageUrl))
            {
                await Launcher.LaunchUriAsync(new Uri(result.ReleasePageUrl));
            }
        }
        catch
        {
            // 起動時チェック失敗は無視
        }
    }
}
