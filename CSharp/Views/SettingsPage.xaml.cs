using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SmartPowerManager.Services;
using Windows.System;

namespace SmartPowerManager.Views;

public sealed partial class SettingsPage : Page
{
    private AppState? _state;
    private bool _isInitializing;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _state = e.Parameter as AppState;
        _isInitializing = true;

        ThemeComboBox.Items.Clear();
        ThemeComboBox.Items.Add(Strings.Get("Theme_System"));
        ThemeComboBox.Items.Add(Strings.Get("Theme_Light"));
        ThemeComboBox.Items.Add(Strings.Get("Theme_Dark"));
        ThemeComboBox.SelectedIndex = _state?.Settings.ThemePreference switch
        {
            AppThemePreference.Light => 1,
            AppThemePreference.Dark => 2,
            _ => 0
        };

        AutoStartToggle.IsOn = _state?.Settings.AutoStart ?? false;
        RefreshAutostartInfo();
        LoadDeviceSettings();
        _isInitializing = false;
    }

    private void LoadDeviceSettings()
    {
        if (_state == null)
            return;

        var pico = _state.ScheduleManager.Data.PicoSettings;
        PicoIpTextBox.Text = pico.Ip;
        GasUrlTextBox.Text = pico.GasUrl;

        if (pico.GasTarget == AppConstants.GasTargetServer)
            GasServerRadio.IsChecked = true;
        else
            GasDesktopRadio.IsChecked = true;

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
                if (MacComboBox.Items[i]?.ToString()?.Contains(selectedMac, StringComparison.OrdinalIgnoreCase) == true)
                {
                    MacComboBox.SelectedIndex = i;
                    return;
                }
            }
        }

        if (MacComboBox.Items.Count > 0)
            MacComboBox.SelectedIndex = 0;
    }

    private void RefreshAutostartInfo()
    {
        AutostartModeText.Text = Strings.Get("Settings_AutostartMode_Task");
        AutostartPathText.Text = StartupManager.GetRegisteredCommand() ?? Strings.Get("Settings_Autostart_NotRegistered");
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _state == null || ThemeComboBox.SelectedIndex < 0)
            return;

        var preference = ThemeComboBox.SelectedIndex switch
        {
            1 => AppThemePreference.Light,
            2 => AppThemePreference.Dark,
            _ => AppThemePreference.System
        };

        ThemeService.SetPreference(preference);
        _state.Settings.ThemePreference = preference;
        _state.Settings.Save();
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _state == null)
            return;

        bool requested = AutoStartToggle.IsOn;
        bool ok = StartupManager.SyncAutostartWithSettings(requested);
        if (!ok && requested)
        {
            _isInitializing = true;
            AutoStartToggle.IsOn = false;
            _isInitializing = false;
            return;
        }

        _state.Settings.AutoStart = requested;
        _state.Settings.Save();
        RefreshAutostartInfo();
    }

    private void SaveDeviceSettings()
    {
        if (_state == null)
            return;

        var pico = _state.ScheduleManager.Data.PicoSettings;
        pico.Ip = PicoIpTextBox.Text.Trim();
        pico.GasUrl = GasUrlTextBox.Text.Trim();
        pico.GasTarget = GasServerRadio.IsChecked == true
            ? AppConstants.GasTargetServer
            : AppConstants.GasTargetDesktop;

        if (MacComboBox.SelectedItem is string selected)
        {
            int idx = selected.LastIndexOf(" - ", StringComparison.Ordinal);
            pico.TargetMac = idx >= 0 ? selected[(idx + 3)..] : selected;
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
