using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SmartPowerManager.Views;

/// <summary>
/// 閉じた状態の ComboBox 上でホイール操作したとき、選択値を前後に切り替える。
/// </summary>
internal static class ComboBoxMouseWheelHelper
{
    public static void Attach(params ComboBox[] boxes)
    {
        foreach (ComboBox box in boxes)
            box.PointerWheelChanged += OnPointerWheelChanged;
    }

    private static void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ComboBox box || box.IsDropDownOpen || box.Items.Count == 0)
            return;

        int delta = e.GetCurrentPoint(box).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        int index = box.SelectedIndex >= 0 ? box.SelectedIndex : 0;
        int next = delta > 0 ? index - 1 : index + 1;
        next = Math.Clamp(next, 0, box.Items.Count - 1);
        if (next != box.SelectedIndex)
            box.SelectedIndex = next;

        e.Handled = true;
    }
}
