using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace SmartPowerManager
{
    /// <summary>
    /// タイトルバー（最小化・最大化・閉じるボタン含む）のテーマ同期。
    /// ExtendsContentIntoTitleBar=false では Transparent が無視されるため、
    /// ボタン背景はタイトルバー背景と同じ不透明色を明示設定する。
    /// </summary>
    public sealed class TitleBarThemeHelper
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        private readonly Window _window;
        private readonly FrameworkElement _themeRoot;
        private UISettings? _uiSettings;
        private bool _updateScheduled;
        private IntPtr _hwnd;

        public TitleBarThemeHelper(Window window, FrameworkElement themeRoot)
        {
            _window = window;
            _themeRoot = themeRoot;

            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            _window.AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;

            _themeRoot.ActualThemeChanged += (_, _) => ScheduleUpdate();
            ThemeService.ThemeChanged += (_, _) => ScheduleUpdate();

            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) =>
            {
                if (ThemeService.CurrentPreference == AppThemePreference.System)
                    ScheduleUpdate();
            };

            _window.Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated)
                    ScheduleUpdate();
            };

            ScheduleUpdate();
        }

        public void ScheduleUpdate()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            if (_updateScheduled)
                return;

            _updateScheduled = true;

            _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                ApplyTitleBarTheme();
                _updateScheduled = false;

                // キャプションボタン背景は 1 フレーム遅れて反映されることがある
                _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, ApplyTitleBarTheme);
            });
        }

        private void ApplyTitleBarTheme()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            bool isDark = ThemeService.IsDarkTheme(_themeRoot);
            var titleBar = _window.AppWindow.TitleBar;

            ApplyPreferredTheme(titleBar);
            ApplyImmersiveDarkMode(isDark);
            ApplyTitleBarColors(titleBar, isDark);
        }

        private static void ApplyPreferredTheme(AppWindowTitleBar titleBar)
        {
            titleBar.PreferredTheme = ThemeService.CurrentPreference switch
            {
                AppThemePreference.Light => TitleBarTheme.Light,
                AppThemePreference.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode
            };
        }

        private static void ApplyTitleBarColors(AppWindowTitleBar titleBar, bool isDark)
        {
            if (isDark)
            {
                var background = Color.FromArgb(255, 32, 32, 32);
                var foreground = Colors.White;
                var inactiveForeground = Color.FromArgb(255, 150, 150, 150);
                var hoverBackground = Color.FromArgb(255, 56, 56, 56);
                var pressedBackground = Color.FromArgb(255, 72, 72, 72);

                titleBar.BackgroundColor = background;
                titleBar.ForegroundColor = foreground;
                titleBar.InactiveBackgroundColor = background;
                titleBar.InactiveForegroundColor = inactiveForeground;

                // 重要: ExtendsContentIntoTitleBar=false では Transparent は無視される
                titleBar.ButtonBackgroundColor = background;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = hoverBackground;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = pressedBackground;
                titleBar.ButtonPressedForegroundColor = foreground;
                titleBar.ButtonInactiveBackgroundColor = background;
                titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            }
            else
            {
                var background = Color.FromArgb(255, 255, 255, 255);
                var foreground = Colors.Black;
                var inactiveForeground = Color.FromArgb(255, 120, 120, 120);
                var hoverBackground = Color.FromArgb(255, 230, 230, 230);
                var pressedBackground = Color.FromArgb(255, 210, 210, 210);

                titleBar.BackgroundColor = background;
                titleBar.ForegroundColor = foreground;
                titleBar.InactiveBackgroundColor = background;
                titleBar.InactiveForegroundColor = inactiveForeground;

                titleBar.ButtonBackgroundColor = background;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = hoverBackground;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = pressedBackground;
                titleBar.ButtonPressedForegroundColor = foreground;
                titleBar.ButtonInactiveBackgroundColor = background;
                titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            }
        }

        private void ApplyImmersiveDarkMode(bool useDarkMode)
        {
            EnsureHwnd();
            if (_hwnd == IntPtr.Zero)
                return;

            int value = useDarkMode ? 1 : 0;
            _ = DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            _ = DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }

        private void EnsureHwnd()
        {
            if (_hwnd == IntPtr.Zero)
                _hwnd = WindowNative.GetWindowHandle(_window);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
