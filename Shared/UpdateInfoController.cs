using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace WinUiShared;

/// <summary>
/// 情報ページの「更新の確認」欄の処理（版の表示・最終確認日時・自動確認のトグル・手動確認・インストール）。
/// 以前は全アプリの InfoPage.xaml.cs に同じ約 100 行がコピーされていた。
/// 見た目（XAML と x:Uid の文言）は各アプリの InfoPage に置いたまま、部品をこのクラスに渡す。
///
/// 使い方: InfoPage のコンストラクタで <c>_update = new UpdateInfoController(new UpdateInfoControls { ... });</c>、
/// OnNavigatedTo で <c>_update.Bind(settings, XamlRoot)</c> を呼ぶ。イベントの登録はこのクラスが行う
/// （XAML 側の Click / Toggled は書かない）。
/// </summary>
public sealed class UpdateInfoController
{
    private readonly UpdateInfoControls _c;
    private IUpdateSettings? _settings;
    private UpdateCheckResult? _lastResult;
    private bool _isInitializing;

    public UpdateInfoController(UpdateInfoControls controls)
    {
        _c = controls;
        if (_c.AutoUpdateToggleCard != null)
            ToggleSwitchClickHelper.BindCardClick(_c.AutoUpdateToggleCard, _c.AutoUpdateToggle);

        _c.CheckUpdateButton.Click += CheckUpdateButton_Click;
        _c.AutoUpdateToggle.Toggled += AutoUpdateToggle_Toggled;
        _c.InstallUpdateButton.Click += InstallUpdateButton_Click;
    }

    /// <summary>ページを開いたとき（OnNavigatedTo）に設定を結び付け、表示を今の値にする。</summary>
    public void Bind(IUpdateSettings? settings)
    {
        _settings = settings;
        _isInitializing = true;

        _c.VersionText.Text = AppHost.FormatString("Version_Format", AppUpdate.CurrentVersion);
        RefreshLastUpdateCheckText();
        _c.AutoUpdateToggle.IsOn = settings?.AutoCheckUpdateOnStartup ?? true;

        _isInitializing = false;
    }

    private void RefreshLastUpdateCheckText()
    {
        _c.LastUpdateCheckText.Text = AppUpdate.FormatLastUpdateCheckDisplay(_settings?.LastUpdateCheckUtc);
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null)
            return;

        _c.CheckUpdateButton.IsEnabled = false;
        _c.UpdateInfoBar.IsOpen = false;
        _c.InstallUpdateCard.Visibility = Visibility.Collapsed;
        _lastResult = null;

        UpdateCheckResult result = await AppUpdate.CheckAndRecordAsync(_settings);
        _lastResult = result;
        RefreshLastUpdateCheckText();

        InfoUpdateUi.ApplyManualCheckResult(
            result,
            _c.UpdateInfoBar,
            _c.InstallUpdateCard,
            _c.InstallStatusText,
            _c.InstallUpdateButton,
            (key, args) => AppHost.FormatString(key, args));

        _c.CheckUpdateButton.IsEnabled = true;

        // 新しい版はあるが setup.exe が添付されていないときは、リリースのページを開くか聞く
        if (result.Status == UpdateCheckStatus.UpdateAvailable
            && (string.IsNullOrWhiteSpace(result.DownloadUrl) || string.IsNullOrWhiteSpace(result.AssetFileName))
            && !string.IsNullOrWhiteSpace(result.ReleasePageUrl)
            && _c.CheckUpdateButton.XamlRoot != null)
        {
            var dialog = new ContentDialog
            {
                Title = AppHost.GetString("Update_AvailableTitle"),
                Content = result.Message,
                PrimaryButtonText = AppHost.GetString("Update_OpenRelease"),
                CloseButtonText = AppHost.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _c.CheckUpdateButton.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Launcher.LaunchUriAsync(new Uri(result.ReleasePageUrl));
        }
    }

    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _settings == null)
            return;

        _settings.AutoCheckUpdateOnStartup = _c.AutoUpdateToggle.IsOn;
        _settings.Save();
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult?.DownloadUrl == null || _lastResult.AssetFileName == null)
            return;

        _c.InstallUpdateButton.IsEnabled = false;
        _c.InstallStatusText.Text = AppHost.GetString("Update_Preparing");

        try
        {
            var progress = new Progress<string>(msg => _c.InstallStatusText.Text = msg);
            string message = await AppUpdate.DownloadAndInstallAsync(
                _lastResult.DownloadUrl,
                _lastResult.AssetFileName,
                progress);
            _c.InstallStatusText.Text = message;
        }
        catch (Exception ex)
        {
            _c.InstallStatusText.Text = AppHost.FormatString("Update_Failed", ex.Message);
            _c.InstallUpdateButton.IsEnabled = true;
        }
    }
}

/// <summary><see cref="UpdateInfoController"/> に渡す、情報ページの部品（XAML の x:Name）。</summary>
public sealed class UpdateInfoControls
{
    public required TextBlock VersionText { get; init; }
    public required TextBlock LastUpdateCheckText { get; init; }
    public required Button CheckUpdateButton { get; init; }
    public required InfoBar UpdateInfoBar { get; init; }

    /// <summary>自動確認のトグルを載せたカード（カード全体のクリックでトグルを切り替える。無ければ null）。</summary>
    public CommunityToolkit.WinUI.Controls.SettingsCard? AutoUpdateToggleCard { get; init; }

    public required ToggleSwitch AutoUpdateToggle { get; init; }
    public required UIElement InstallUpdateCard { get; init; }
    public required TextBlock InstallStatusText { get; init; }
    public required Button InstallUpdateButton { get; init; }
}
