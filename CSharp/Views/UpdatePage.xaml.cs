using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SmartPowerManager.Services;
using Windows.System;

namespace SmartPowerManager.Views;

public sealed partial class UpdatePage : Page
{
    private UpdateCheckResult? _lastResult;

    public UpdatePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        VersionText.Text = Strings.Format("Settings_CurrentVersion", UpdateChecker.CurrentVersion);
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateInfoBar.IsOpen = false;
        InstallUpdateCard.Visibility = Visibility.Collapsed;
        _lastResult = null;

        var result = await UpdateChecker.CheckForUpdateAsync();
        _lastResult = result;
        UpdateInfoBar.Message = result.Message;
        UpdateInfoBar.IsOpen = true;
        UpdateInfoBar.Severity = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => InfoBarSeverity.Informational,
            UpdateCheckStatus.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Success
        };

        CheckUpdateButton.IsEnabled = true;

        if (result.Status == UpdateCheckStatus.UpdateAvailable)
        {
            if (!string.IsNullOrWhiteSpace(result.DownloadUrl) && !string.IsNullOrWhiteSpace(result.AssetFileName))
            {
                InstallUpdateCard.Visibility = Visibility.Visible;
                InstallStatusText.Text = $"バージョン {result.LatestVersion} をダウンロードできます";
            }
            else if (!string.IsNullOrWhiteSpace(result.ReleasePageUrl))
            {
                var dialog = new ContentDialog
                {
                    Title = Strings.Get("Update_AvailableTitle"),
                    Content = result.Message,
                    PrimaryButtonText = Strings.Get("Update_OpenRelease"),
                    CloseButtonText = Strings.Get("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await Launcher.LaunchUriAsync(new Uri(result.ReleasePageUrl));
            }
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult?.DownloadUrl == null || _lastResult.AssetFileName == null)
            return;

        InstallUpdateButton.IsEnabled = false;
        InstallStatusText.Text = "準備中...";

        try
        {
            var progress = new Progress<string>(msg => InstallStatusText.Text = msg);
            string message = await UpdateInstallerService.DownloadAndInstallAsync(
                _lastResult.DownloadUrl,
                _lastResult.AssetFileName,
                progress);
            InstallStatusText.Text = message;
        }
        catch (Exception ex)
        {
            InstallStatusText.Text = $"更新失敗: {ex.Message}";
            InstallUpdateButton.IsEnabled = true;
        }
    }
}
