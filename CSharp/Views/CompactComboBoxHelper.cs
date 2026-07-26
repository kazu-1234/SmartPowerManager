using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SmartPowerManager.Views;

/// <summary>
/// WinUI ComboBox 既定テンプレートでは内容列が * のため、文字と矢印のあいだに余白が開く。
/// 内容列を Auto にし、矢印列幅・Margin を詰めて文字の直後に矢印が来るようにする。
/// </summary>
internal static class CompactComboBoxHelper
{
    private const double GlyphColumnWidth = 20;
    private static readonly Thickness GlyphMargin = new(0, 0, 6, 0);

    public static void Attach(params ComboBox[] boxes)
    {
        foreach (ComboBox box in boxes)
        {
            if (box.IsLoaded)
                Apply(box);
            box.Loaded += OnLoaded;
            box.SizeChanged += OnSizeChanged;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox box)
            Apply(box);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ComboBox box)
            Apply(box);
    }

    private static void Apply(ComboBox box)
    {
        box.ApplyTemplate();
        box.UpdateLayout();

        if (FindDescendant(box, "DropDownGlyph") is FrameworkElement glyph)
            glyph.Margin = GlyphMargin;

        // Col0=* が文字〜矢印の隙間の本体。Auto にして文字幅だけにする。
        if (FindDescendant(box, "LayoutRoot") is Grid { ColumnDefinitions.Count: >= 2 } layoutRoot)
        {
            layoutRoot.ColumnDefinitions[0].Width = GridLength.Auto;
            layoutRoot.ColumnDefinitions[1].Width = new GridLength(GlyphColumnWidth);
        }
    }

    private static FrameworkElement? FindDescendant(DependencyObject parent, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: { } childName } fe && childName == name)
                return fe;

            FrameworkElement? nested = FindDescendant(child, name);
            if (nested != null)
                return nested;
        }

        return null;
    }
}
