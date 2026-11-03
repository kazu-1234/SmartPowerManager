using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SmartPowerManager.Services;
using Windows.System;
using WinUiShared;

namespace SmartPowerManager.Views;

/// <summary>
/// 設定ページ。テーマ選択と自動起動欄の処理は共通部（ThemeComboBoxBinder / AutostartSettingsController）が行う。
/// Pico（デバイス）の設定欄はこのアプリ固有。
/// </summary>
public sealed partial class SettingsPage : Page
{
    // テーマの項目の文言キーの頭（このアプリは Theme_System / Theme_Light / Theme_Dark）
    private const string ThemeItemKeyPrefix = "Theme_";

    private AppState? _state;
    private readonly ThemeComboBoxBinder _theme;
    private readonly AutostartSettingsController _autostart;

    public SettingsPage()
    {
        InitializeComponent();
        _theme = new ThemeComboBoxBinder(
            ThemeComboBox,
            get: () => _state?.Settings.ThemePreference ?? AppThemePreference.System,
            set: value => { if (_state != null) _state.Settings.ThemePreference = value; },
            save: () => _state?.Settings.Save(),
            keyPrefix: ThemeItemKeyPrefix);

        _autostart = new AutostartSettingsController(new AutostartSettingsControls
        {
            AutoStartToggle = AutoStartToggle,
            Expander = AutoStartExpander,
            UseLogonTaskCheckBox = AutostartTaskOnlyCheckBox,
            TypeLine = AutostartTypeLine,
            PathLine = AutostartPathLine,
            RefreshButton = RefreshAutostartButton,
            HideTrayIconCheckBox = HideTrayIconCheckBox,
            ShowStartupNotificationCheckBox = ShowStartupNotificationCheckBox
        }, AppSetup.Autostart);
        AutostartExpandHelper.AttachSkipInitialAnimation(DeviceExpander);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _state = e.Parameter as AppState;
        if (_state == null)
            return;

        _theme.Load();
        _autostart.Bind(AutostartSettingsAccess.For(_state.Settings, () => _state.ApplyTrayIconVisibility?.Invoke()));
        LoadDeviceSettings();
    }

    private void LoadDeviceSettings()
    {
        if (_state == null)
            return;

        var pico = _state.ScheduleManager.Data.PicoSettings;
        PicoIpTextBox.Text = pico.Ip;
        RefreshMacList(pico.TargetMac);
    }

    private void RefreshMacList(string? selectedMac = null)
    {
        var macs = MacAddressService.GetMacAddresses();
        MacComboBox.Items.Clear();
        foreach (var item in macs)
            MacComboBox.Items.Add($"{item.Name} - {item.Mac}");

        if (!string.IsNullOrWhiteSpace(selectedMac))
        {
            for (int i = 0; i < MacComboBox.Items.Count; i++)
            {
                string? text = MacComboBox.Items[i]?.ToString();
                if (text != null && text.EndsWith(selectedMac, StringComparison.OrdinalIgnoreCase))
                {
                    MacComboBox.SelectedIndex = i;
                    return;
                }
            }

            // 保存済み MAC が検出一覧に無い場合も選べるように追加
            MacComboBox.Items.Insert(0, selectedMac.ToUpperInvariant());
            MacComboBox.SelectedIndex = 0;
            return;
        }

        if (MacComboBox.Items.Count > 0)
            MacComboBox.SelectedIndex = 0;
    }

    private void SaveDeviceSettings()
    {
        if (_state == null)
            return;

        var pico = _state.ScheduleManager.Data.PicoSettings;
        pico.Ip = PicoIpTextBox.Text.Trim();

        if (MacComboBox.SelectedItem is string selected)
        {
            int idx = selected.LastIndexOf(" - ", StringComparison.Ordinal);
            pico.TargetMac = idx >= 0 ? selected[(idx + 3)..].Trim() : selected.Trim();
        }

        _state.ScheduleManager.Save();
        _ = _state.SyncDevicesAsync(silent: true);
    }

    private void RefreshMacButton_Click(object sender, RoutedEventArgs e)
    {
        string? current = _state?.ScheduleManager.Data.PicoSettings.TargetMac;
        RefreshMacList(current);
        SaveDeviceSettings();
    }

    private async void OpenPicoWebButton_Click(object sender, RoutedEventArgs e)
    {
        string ip = PicoIpTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip) || ip == AppConstants.DefaultPicoIp)
            return;

        SaveDeviceSettings();
        await Launcher.LaunchUriAsync(new Uri($"http://{ip}/"));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        SaveDeviceSettings();
        base.OnNavigatedFrom(e);
    }
}
