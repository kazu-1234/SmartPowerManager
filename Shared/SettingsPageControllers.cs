using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUiShared;

/// <summary>
/// 設定ページのテーマ選択（システム / ライト / ダーク）。以前は全アプリの SettingsPage にコピーされていた。
/// 項目の並びと設定値を明示的に対応付ける（enum の数値をそのまま添字にすると、並び替えや範囲外の値で
/// 別の項目が選ばれたり例外になったりするため。MusicPlayer / LeftHandDevice がその書き方だった）。
/// </summary>
public sealed class ThemeComboBoxBinder
{
    private readonly ComboBox _box;
    private readonly Func<AppThemePreference> _get;
    private readonly Action<AppThemePreference> _set;
    private readonly Action _save;
    private readonly string _keyPrefix;
    private bool _isInitializing;

    /// <summary>項目の文言キーの既定の頭（Settings_Theme_System / _Light / _Dark）。</summary>
    public const string DefaultKeyPrefix = "Settings_Theme_";

    /// <param name="box">テーマの ComboBox（XAML 側に SelectionChanged は書かない）。</param>
    /// <param name="get">今の設定値を返す。</param>
    /// <param name="set">設定値を書き換える（保存は save で行う）。</param>
    /// <param name="save">設定を保存する。</param>
    /// <param name="fitToSelectedText">選択中の文言に合わせて幅を伸縮させるか（ほとんどのアプリは true）。</param>
    /// <param name="keyPrefix">
    /// 項目の文言キーの頭。後ろに System / Light / Dark を付けて .resw から引く
    /// （SmartPowerManager は "Theme_"。表示を変えないよう、アプリが今使っているキーを渡す）。
    /// </param>
    public ThemeComboBoxBinder(
        ComboBox box,
        Func<AppThemePreference> get,
        Action<AppThemePreference> set,
        Action save,
        bool fitToSelectedText = true,
        string keyPrefix = DefaultKeyPrefix)
    {
        _box = box;
        _get = get;
        _set = set;
        _save = save;
        _keyPrefix = keyPrefix;
        if (fitToSelectedText)
            CompactComboBoxHelper.AttachFitToSelectedText(box);
        box.SelectionChanged += Box_SelectionChanged;
    }

    /// <summary>項目を入れ直し、今の設定値を選ぶ（ページを開いたときに呼ぶ）。</summary>
    public void Load()
    {
        _isInitializing = true;
        _box.Items.Clear();
        _box.Items.Add(AppHost.GetString(_keyPrefix + "System"));
        _box.Items.Add(AppHost.GetString(_keyPrefix + "Light"));
        _box.Items.Add(AppHost.GetString(_keyPrefix + "Dark"));
        _box.SelectedIndex = ToIndex(_get());
        _isInitializing = false;
    }

    /// <summary>設定値 → 項目の位置（知らない値はシステムに寄せる）。</summary>
    public static int ToIndex(AppThemePreference preference) => preference switch
    {
        AppThemePreference.Light => 1,
        AppThemePreference.Dark => 2,
        _ => 0
    };

    /// <summary>項目の位置 → 設定値。</summary>
    public static AppThemePreference FromIndex(int index) => index switch
    {
        1 => AppThemePreference.Light,
        2 => AppThemePreference.Dark,
        _ => AppThemePreference.System
    };

    private void Box_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _box.SelectedIndex < 0)
            return;

        AppThemePreference preference = FromIndex(_box.SelectedIndex);
        if (preference == _get())
            return;

        ThemeService.SetPreference(preference);
        _set(preference);
        _save();
    }
}

/// <summary>
/// 設定ページの「自動起動」欄（トグル、タスク / スタートアップの切り替えと失敗時の巻き戻し、登録情報の表示、
/// トレイアイコンを隠す・起動時の通知のチェック）。以前は自動起動のある全アプリに約 130 行がコピーされていた。
/// 部品はアプリの XAML にあるものを渡す（無い部品は null。XAML 側にイベントは書かない）。
/// </summary>
public sealed class AutostartSettingsController
{
    private readonly AutostartSettingsControls _c;
    private readonly IAutostartController _autostart;
    private AutostartSettingsAccess? _settings;
    private bool _isInitializing;

    public AutostartSettingsController(AutostartSettingsControls controls, IAutostartController autostart)
    {
        _c = controls;
        _autostart = autostart;

        ToggleSwitchClickHelper.ProtectFromParentCapture(_c.AutoStartToggle);
        if (_c.Expander != null)
            AutostartExpandHelper.AttachSkipInitialAnimation(_c.Expander);

        _c.AutoStartToggle.Toggled += AutoStartToggle_Toggled;
        if (_c.UseLogonTaskCheckBox != null)
        {
            _c.UseLogonTaskCheckBox.Checked += UseLogonTaskCheckBox_Changed;
            _c.UseLogonTaskCheckBox.Unchecked += UseLogonTaskCheckBox_Changed;
        }
        if (_c.RefreshButton != null)
            _c.RefreshButton.Click += RefreshButton_Click;
        if (_c.HideTrayIconCheckBox != null)
        {
            _c.HideTrayIconCheckBox.Checked += HideTrayIconCheckBox_Changed;
            _c.HideTrayIconCheckBox.Unchecked += HideTrayIconCheckBox_Changed;
        }
        if (_c.ShowStartupNotificationCheckBox != null)
        {
            _c.ShowStartupNotificationCheckBox.Checked += ShowStartupNotificationCheckBox_Changed;
            _c.ShowStartupNotificationCheckBox.Unchecked += ShowStartupNotificationCheckBox_Changed;
        }
    }

    /// <summary>ページを開いたときに設定を結び付け、表示を今の値にする。</summary>
    public void Bind(AutostartSettingsAccess settings)
    {
        _settings = settings;
        _isInitializing = true;

        _c.AutoStartToggle.IsOn = settings.GetAutoStart();
        if (_c.UseLogonTaskCheckBox != null)
            _c.UseLogonTaskCheckBox.IsChecked = settings.GetUseLogonTask();
        if (_c.HideTrayIconCheckBox != null && settings.GetHideTrayIcon != null)
            _c.HideTrayIconCheckBox.IsChecked = settings.GetHideTrayIcon();
        if (_c.ShowStartupNotificationCheckBox != null && settings.GetShowStartupNotification != null)
            _c.ShowStartupNotificationCheckBox.IsChecked = settings.GetShowStartupNotification();
        if (_c.Expander != null && _c.ExpandOnLoad)
            _c.Expander.IsExpanded = true;
        RefreshInfo();

        _isInitializing = false;
    }

    /// <summary>登録情報（種類とパス）の表示を、今の設定と登録に合わせ直す。</summary>
    public void RefreshInfo()
    {
        if (_c.TypeLine == null && _c.PathLine == null)
            return;

        bool enabled = _settings?.GetAutoStart() ?? false;
        bool useLogonTask = _settings?.GetUseLogonTask() ?? true;

        // 種類（タスク / スタートアップ）とパスは、今選んでいる方式で表示する
        if (_c.TypeLine != null)
        {
            _c.TypeLine.Text = AutostartInfoFormatter.FormatTypeLine(
                enabled, useLogonTask, AppHost.GetString, (key, args) => AppHost.FormatString(key, args));
        }

        if (_c.PathLine != null)
        {
            _c.PathLine.Text = AutostartInfoFormatter.FormatPathLine(
                enabled,
                _autostart.GetRegisteredCommand(preferLogonTask: useLogonTask),
                AppHost.GetString,
                (key, args) => AppHost.FormatString(key, args));
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null)
            return;

        if (_settings.GetAutoStart())
            _autostart.Sync(true, _settings.GetUseLogonTask());

        RefreshInfo();
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _settings == null)
            return;

        bool requested = _c.AutoStartToggle.IsOn;
        if (!_autostart.Sync(requested, _settings.GetUseLogonTask()) && requested)
        {
            // 登録に失敗したらトグルを戻す（設定と実際の登録がずれないように）
            _isInitializing = true;
            _c.AutoStartToggle.IsOn = false;
            _isInitializing = false;
            return;
        }

        _settings.SetAutoStart(requested);
        _settings.Save();
        RefreshInfo();
    }

    private void UseLogonTaskCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _settings == null || _c.UseLogonTaskCheckBox == null || _settings.SetUseLogonTask == null)
            return;

        bool useLogonTask = _c.UseLogonTaskCheckBox.IsChecked == true;
        if (useLogonTask == _settings.GetUseLogonTask())
            return;

        _settings.SetUseLogonTask(useLogonTask);

        // 自動起動が ON のときだけ、登録を新しい方式へ付け替える（OFF なら次に ON にしたとき反映される）
        if (_settings.GetAutoStart() && !_autostart.Sync(true, useLogonTask))
        {
            // 切替失敗時は元の方式に戻す（設定と実際の登録がずれないように）
            _isInitializing = true;
            _settings.SetUseLogonTask(!useLogonTask);
            _c.UseLogonTaskCheckBox.IsChecked = !useLogonTask;
            _isInitializing = false;
            return;
        }

        _settings.Save();
        RefreshInfo();
    }

    private void HideTrayIconCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _settings?.GetHideTrayIcon == null || _settings.SetHideTrayIcon == null
            || _c.HideTrayIconCheckBox == null)
            return;

        bool hide = _c.HideTrayIconCheckBox.IsChecked == true;
        if (hide == _settings.GetHideTrayIcon())
            return;

        _settings.SetHideTrayIcon(hide);
        _settings.Save();
        _settings.ApplyTrayIconVisibility?.Invoke();
    }

    /// <summary>起動時の「常駐準備完了」通知の ON/OFF を保存する（次回起動から反映）。</summary>
    private void ShowStartupNotificationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _settings?.GetShowStartupNotification == null
            || _settings.SetShowStartupNotification == null || _c.ShowStartupNotificationCheckBox == null)
            return;

        bool show = _c.ShowStartupNotificationCheckBox.IsChecked == true;
        if (show == _settings.GetShowStartupNotification())
            return;

        _settings.SetShowStartupNotification(show);
        _settings.Save();
    }
}

/// <summary><see cref="AutostartSettingsController"/> に渡す、設定ページの部品（XAML の x:Name。無いものは null）。</summary>
public sealed class AutostartSettingsControls
{
    public required ToggleSwitch AutoStartToggle { get; init; }
    public SettingsExpander? Expander { get; init; }

    /// <summary>ページを開いたときに自動起動の欄を開いておくか（テンプレートは true）。</summary>
    public bool ExpandOnLoad { get; init; } = true;

    public CheckBox? UseLogonTaskCheckBox { get; init; }
    public TextBlock? TypeLine { get; init; }
    public TextBlock? PathLine { get; init; }
    public Button? RefreshButton { get; init; }
    public CheckBox? HideTrayIconCheckBox { get; init; }
    public CheckBox? ShowStartupNotificationCheckBox { get; init; }
}

/// <summary>
/// 自動起動の欄が読み書きする設定（アプリの Settings のプロパティを関数で渡す）。
/// 項目を持たないアプリは、その Get / Set を null にする（例: タスク専用なら SetUseLogonTask = null）。
/// </summary>
public sealed class AutostartSettingsAccess
{
    public required Func<bool> GetAutoStart { get; init; }
    public required Action<bool> SetAutoStart { get; init; }
    public Func<bool> GetUseLogonTask { get; init; } = () => true;
    public Action<bool>? SetUseLogonTask { get; init; }
    public Func<bool>? GetHideTrayIcon { get; init; }
    public Action<bool>? SetHideTrayIcon { get; init; }
    public Func<bool>? GetShowStartupNotification { get; init; }
    public Action<bool>? SetShowStartupNotification { get; init; }

    /// <summary>トレイアイコンの表示を設定に合わせ直す（AppRuntime.ApplyTrayIconVisibility）。</summary>
    public Action? ApplyTrayIconVisibility { get; init; }

    public required Action Save { get; init; }

    /// <summary>AppHostSettingsBase を使うアプリ向けの簡単な作り方（全項目を結び付ける）。</summary>
    public static AutostartSettingsAccess For(AppHostSettingsBase s, Action? applyTrayIconVisibility = null) => new()
    {
        GetAutoStart = () => s.AutoStart,
        SetAutoStart = v => s.AutoStart = v,
        GetUseLogonTask = () => s.UseLogonTask,
        SetUseLogonTask = v => s.UseLogonTask = v,
        GetHideTrayIcon = () => s.HideTrayIcon,
        SetHideTrayIcon = v => s.HideTrayIcon = v,
        GetShowStartupNotification = () => s.ShowStartupNotification,
        SetShowStartupNotification = v => s.ShowStartupNotification = v,
        ApplyTrayIconVisibility = applyTrayIconVisibility,
        Save = s.Save
    };
}
