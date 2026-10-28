using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace WinUiShared;

/// <summary>
/// Auto Dark Mode 方式のタイトルバーテーマ同期（SPM / BlueShift / Template 正本）。
/// ExtendsContentIntoTitleBar + SetTitleBar + ButtonHoverBackgroundColor のみで実現する。
/// オフスクリーン移動やキャプションボタンの個別色付け（背景・前景・押下色など）のハックは行わない。
/// </summary>
public sealed class TitleBarThemeHelper
{
    // キャプションボタンのホバー時に重ねる半透明色（ダーク時は白、ライト時は黒をうっすら）
    private static readonly Color DarkHoverColor = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color LightHoverColor = Color.FromArgb(40, 0, 0, 0);

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
        ThemeService.ThemeChanged += OnThemeChanged;
        // ThemeChanged は static イベントなので、解除しないと閉じたウィンドウが参照され続ける。
        // Window は都度 new / Close する設計のため、閉じたら必ず購読を外す。
        window.Closed += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
        ApplyCaptionHoverColor();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyCaptionHoverColor();

    public void ApplyCaptionHoverColor()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        bool isDark = ThemeService.IsDarkTheme(_themeRoot);
        _window.AppWindow.TitleBar.ButtonHoverBackgroundColor = isDark ? DarkHoverColor : LightHoverColor;
    }

    private void ApplyTitleBarBinding()
    {
        _window.ExtendsContentIntoTitleBar = true;
        _window.SetTitleBar(_titleBar);
    }
}
