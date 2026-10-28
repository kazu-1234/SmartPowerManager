using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace WinUiShared;

/// <summary>
/// アプリ全体のテーマ（ライト / ダーク / システム連動）を管理する（SPM / BlueShift / Template 正本）。
/// 設定の保存は行わない。呼び出し側（設定画面）が AppState の Settings に書いて保存する。
/// </summary>
public static class ThemeService
{
    private static UISettings? _uiSettings;
    private static bool _isWatchingSystemTheme;
    private static FrameworkElement? _themeRoot;
    private static AppThemePreference _currentPreference = AppThemePreference.System;

    // ApplyThemeToElement で適用した追加ウィンドウのルート。OS テーマ変更時にも追従させるため保持する。
    // 弱参照なので、閉じたウィンドウの要素は GC で回収され、ここに残り続けない。
    private static readonly List<WeakReference<FrameworkElement>> _extraElements = [];

    public static AppThemePreference CurrentPreference => _currentPreference;

    public static event EventHandler? ThemeChanged;

    public static void Initialize(AppThemePreference preference)
    {
        _currentPreference = preference;
    }

    /// <summary>テーマルート（MainWindow の RootGrid 等）を登録し、初回適用する。</summary>
    public static void AttachRoot(FrameworkElement themeRoot)
    {
        _themeRoot = themeRoot;
        EnsureSystemThemeWatcher();
        // 初回は通知しない（起動直後の二重描画を防ぐ）
        ApplyToRoot(notify: false);
    }

    /// <summary>追加ウィンドウなど、メイン以外の要素へ現在のテーマを適用する。</summary>
    public static void ApplyThemeToElement(FrameworkElement element)
    {
        element.RequestedTheme = ResolveElementTheme();

        // 同じ要素の二重登録を避けつつ、回収済みの弱参照はここで掃除する
        _extraElements.RemoveAll(w => !w.TryGetTarget(out var e) || ReferenceEquals(e, element));
        _extraElements.Add(new WeakReference<FrameworkElement>(element));
    }

    /// <summary>テーマ設定を切り替えて即時反映する（保存は呼び出し側で行う）。</summary>
    public static void SetPreference(AppThemePreference preference)
    {
        _currentPreference = preference;
        ApplyToRoot(notify: true);
    }

    public static bool IsDarkTheme(FrameworkElement themeRoot)
    {
        return themeRoot.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => IsSystemDarkTheme()
        };
    }

    /// <summary>
    /// 設定値から要素に代入する ElementTheme を決める。
    /// System のときに ElementTheme.Default を使わない理由: WinUI 3 では Light/Dark を明示した要素を
    /// Default に戻しても ThemeResource が再評価されず、直前のライト/ダークのまま残ることがある。
    /// そのため OS の現在テーマを判定して Light/Dark を明示し、OS 側の変更は ColorValuesChanged で追従する。
    /// </summary>
    private static ElementTheme ResolveElementTheme()
    {
        return _currentPreference switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => IsSystemDarkTheme() ? ElementTheme.Dark : ElementTheme.Light
        };
    }

    private static void ApplyToRoot(bool notify)
    {
        if (_themeRoot == null)
            return;

        ElementTheme theme = ResolveElementTheme();
        _themeRoot.RequestedTheme = theme;

        // 追加ウィンドウにも同じテーマを配る（閉じて回収済みのものは除去）
        _extraElements.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in _extraElements)
        {
            if (weak.TryGetTarget(out var element))
                element.RequestedTheme = theme;
        }

        if (notify)
            ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void EnsureSystemThemeWatcher()
    {
        if (_isWatchingSystemTheme)
            return;

        _isWatchingSystemTheme = true;
        // IsSystemDarkTheme が先に生成している場合もあるので、無ければ作って購読する
        _uiSettings ??= new UISettings();
        // ColorValuesChanged は UI スレッド外から来るため、DispatcherQueue で UI スレッドに戻して適用する。
        // System 選択中は RequestedTheme を Light/Dark で明示しているので、OS 変更時は再適用が必要。
        _uiSettings.ColorValuesChanged += (_, _) =>
        {
            if (_currentPreference != AppThemePreference.System || _themeRoot == null)
                return;

            _themeRoot.DispatcherQueue.TryEnqueue(() =>
            {
                if (_currentPreference == AppThemePreference.System)
                    ApplyToRoot(notify: true);
            });
        };
    }

    private static bool IsSystemDarkTheme()
    {
        // UISettings は生成コストがあるため、監視用インスタンスを使い回す
        _uiSettings ??= new UISettings();
        var background = _uiSettings.GetColorValue(UIColorType.Background);
        return background.R < 128;
    }
}
