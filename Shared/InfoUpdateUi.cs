using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUiShared;

/// <summary>Info ページの更新確認結果表示（SPM 正本）。</summary>
public static class InfoUpdateUi
{
    public static InfoBarSeverity ToSeverity(UpdateCheckStatus status) => status switch
    {
        UpdateCheckStatus.UpdateAvailable => InfoBarSeverity.Informational,
        UpdateCheckStatus.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Success
    };

    public static void ApplyManualCheckResult(
        UpdateCheckResult result,
        InfoBar infoBar,
        UIElement installCard,
        TextBlock installStatusText,
        Control installButton,
        Func<string, object[], string> formatString)
    {
        infoBar.Message = result.Message;
        infoBar.IsOpen = true;
        infoBar.Severity = ToSeverity(result.Status);
        installCard.Visibility = Visibility.Collapsed;

        if (result.Status != UpdateCheckStatus.UpdateAvailable)
            return;

        if (string.IsNullOrWhiteSpace(result.DownloadUrl) || string.IsNullOrWhiteSpace(result.AssetFileName))
            return;

        installCard.Visibility = Visibility.Visible;
        installStatusText.Text = formatString(
            "Update_DownloadReady",
            new object[] { result.LatestVersion ?? string.Empty });
        installButton.IsEnabled = true;
    }
}
