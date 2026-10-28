using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinUiShared;

/// <summary>
/// ToggleSwitch が SettingsCard / SettingsExpander / ScrollViewer 配下でも
/// クリックで確実に切り替わるようにする（SPM / BlueShift / Template 正本）。
/// スイッチ自身の PointerPressed を Handled にしてはいけない（本体が切り替わらなくなる）。
/// </summary>
public static class ToggleSwitchClickHelper
{
    private static readonly ConditionalWeakTable<SettingsCard, ToggleSwitch> CardToggleMap = new();

    /// <summary>
    /// SettingsCard の行クリックでトグルを反転する。
    /// スイッチ本体クリックとの二重反転は OriginalSource で抑止する。
    /// </summary>
    public static void BindCardClick(SettingsCard card, ToggleSwitch toggle)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(toggle);

        card.IsClickEnabled = true;
        card.IsActionIconVisible = false;
        ProtectFromParentCapture(toggle);

        if (CardToggleMap.TryGetValue(card, out _))
            CardToggleMap.Remove(card);
        else
            card.Click += Card_Click;

        CardToggleMap.Add(card, toggle);
    }

    /// <summary>
    /// ScrollViewer との微小ドラッグ競合を緩和する。
    /// Pointer を Handled にしない（ToggleSwitch 本体のクリックを潰すため）。
    /// </summary>
    public static void ProtectFromParentCapture(ToggleSwitch toggle)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        toggle.ManipulationMode = ManipulationModes.System;
    }

    private static void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard card || !CardToggleMap.TryGetValue(card, out ToggleSwitch? toggle))
            return;

        if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, toggle))
            return;

        toggle.IsOn = !toggle.IsOn;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }
}
