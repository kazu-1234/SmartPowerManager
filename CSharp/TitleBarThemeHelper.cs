using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace SmartPowerManager
{
    /// <summary>
    /// Auto Dark Mode 方式: WinUI TitleBar + ButtonHoverBackgroundColor のみ。
    /// </summary>
    public sealed class TitleBarThemeHelper
    {
        private readonly Window _window;
        private readonly FrameworkElement _themeRoot;
        private readonly TitleBar _titleBar;

        public TitleBarThemeHelper(Window window, FrameworkElement themeRoot, TitleBar titleBar)
        {
            _window = window;
            _themeRoot = themeRoot;
            _titleBar = titleBar;

            ApplyTitleBarBinding();
            titleBar.ActualThemeChanged += (_, _) => ApplyCaptionHoverColor();
            ThemeService.ThemeChanged += (_, _) => ApplyCaptionHoverColor();
            ApplyCaptionHoverColor();
        }

        public void ApplyCaptionHoverColor()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            bool isDark = ThemeService.IsDarkTheme(_themeRoot);
            var hover = isDark
                ? Color.FromArgb(20, 255, 255, 255)
                : Color.FromArgb(40, 0, 0, 0);

            _window.AppWindow.TitleBar.ButtonHoverBackgroundColor = hover;
        }

        private void ApplyTitleBarBinding()
        {
            _window.ExtendsContentIntoTitleBar = true;
            _window.SetTitleBar(_titleBar);
        }
    }
}
