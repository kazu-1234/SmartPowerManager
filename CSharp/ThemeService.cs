using Microsoft.UI.Xaml;
using System;
using Windows.UI.ViewManagement;

namespace SmartPowerManager
{
    /// <summary>
    /// アプリ全体のテーマ（ライト / ダーク / システム連動）を管理する。
    /// </summary>
    public static class ThemeService
    {
        private static UISettings? _uiSettings;
        private static FrameworkElement? _themeRoot;
        private static AppThemePreference _currentPreference = AppThemePreference.System;

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
            ApplyToRoot(save: false, notify: false);
        }

        /// <summary>追加ウィンドウなど、メイン以外の要素へ現在のテーマを適用する。</summary>
        public static void ApplyThemeToElement(FrameworkElement element)
        {
            element.RequestedTheme = _currentPreference switch
            {
                AppThemePreference.Light => ElementTheme.Light,
                AppThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        public static void SetPreference(AppThemePreference preference, bool save = true)
        {
            _currentPreference = preference;
            ApplyToRoot(save, notify: true);
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

        private static void ApplyToRoot(bool save, bool notify)
        {
            if (_themeRoot == null)
                return;

            _themeRoot.RequestedTheme = _currentPreference switch
            {
                AppThemePreference.Light => ElementTheme.Light,
                AppThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            if (notify)
                ThemeChanged?.Invoke(null, EventArgs.Empty);

            if (save)
            {
                var settings = Settings.Load();
                settings.ThemePreference = _currentPreference;
                settings.Save();
            }
        }

        private static void EnsureSystemThemeWatcher()
        {
            if (_uiSettings != null)
                return;

            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) =>
            {
                if (_currentPreference != AppThemePreference.System || _themeRoot == null)
                    return;

                _themeRoot.DispatcherQueue.TryEnqueue(() =>
                {
                    ThemeChanged?.Invoke(null, EventArgs.Empty);
                });
            };
        }

        private static bool IsSystemDarkTheme()
        {
            var background = new UISettings().GetColorValue(UIColorType.Background);
            return background.R < 128;
        }
    }
}
