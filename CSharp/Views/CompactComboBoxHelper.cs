using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace SmartPowerManager.Views;

/// <summary>
/// WinUI ComboBox 既定テンプレートでは内容列が * のため、文字と矢印のあいだに余白が開く。
/// 内容列を Auto にし、矢印列幅・Margin を詰めて文字の直後に矢印が来るようにする。
/// テーマ ComboBox など可変幅の場合は、選択中テキスト幅に合わせて Width を設定する。
/// </summary>
internal static class CompactComboBoxHelper
{
    private const double GlyphColumnWidth = 20;
    private const double BorderThicknessTotal = 2;
    private static readonly Thickness GlyphMargin = new(0, 0, 6, 0);
    private static readonly HashSet<ComboBox> FitToTextBoxes = [];
    private static readonly HashSet<ComboBox> Applying = [];

    public static void Attach(params ComboBox[] boxes)
    {
        foreach (ComboBox box in boxes)
        {
            if (box.IsLoaded)
                Apply(box);
            box.Loaded += OnLoaded;
            box.SizeChanged += OnSizeChanged;
            box.SelectionChanged += OnSelectionChanged;
        }
    }

    /// <summary>
    /// 選択中の文言に合わせて ComboBox 幅を伸縮させる（テーマ ComboBox 用）。
    /// </summary>
    public static void AttachFitToSelectedText(ComboBox box)
    {
        FitToTextBoxes.Add(box);
        box.Loaded += OnFitLoaded;
        box.SizeChanged += OnFitSizeChanged;
        box.SelectionChanged += OnFitSelectionChanged;
        box.ActualThemeChanged += OnFitActualThemeChanged;
        if (box.IsLoaded)
            FitWidthToSelectedText(box);
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

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box)
            Apply(box);
    }

    private static void OnFitLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox box)
            FitWidthToSelectedText(box);
    }

    private static void OnFitSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ComboBox box)
            Apply(box);
    }

    private static void OnFitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box)
            FitWidthToSelectedText(box);
    }

    private static void OnFitActualThemeChanged(FrameworkElement sender, object args)
    {
        if (sender is ComboBox box)
            Apply(box);
    }

    private static void FitWidthToSelectedText(ComboBox box)
    {
        string? text = box.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(text))
            return;

        var measureBlock = new TextBlock
        {
            Text = text,
            FontSize = box.FontSize,
            FontFamily = box.FontFamily,
            FontWeight = box.FontWeight,
            FontStyle = box.FontStyle,
            FontStretch = box.FontStretch
        };
        measureBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double sidePadding = box.Padding.Left;
        double contentWidth = measureBlock.DesiredSize.Width;
        // 文字の左右余白は同じ（Padding.Left = ContentPresenter 右 Margin）。
        double width = contentWidth
            + sidePadding
            + sidePadding
            + GlyphColumnWidth
            + GlyphMargin.Right
            + BorderThicknessTotal;

        double newWidth = Math.Ceiling(width);
        if (double.IsNaN(box.Width) || Math.Abs(box.Width - newWidth) > 0.5)
            box.Width = newWidth;

        box.MinWidth = 0;
        box.ClearValue(FrameworkElement.MaxWidthProperty);

        Apply(box);
    }

    private static void Apply(ComboBox box)
    {
        if (!Applying.Add(box))
            return;

        try
        {
            // ApplyTemplate は視覚ツリーを作り直して切替時に点滅する。未展開時のみ。
            if (FindDescendant(box, "LayoutRoot") == null)
                box.ApplyTemplate();

            if (FindDescendant(box, "DropDownGlyph") is FrameworkElement glyph)
                glyph.Margin = GlyphMargin;

            if (FitToTextBoxes.Contains(box)
                && FindDescendant(box, "ContentPresenter") is FrameworkElement content)
            {
                double side = box.Padding.Left;
                content.Margin = new Thickness(side, box.Padding.Top, side, box.Padding.Bottom);
            }

            if (FindDescendant(box, "LayoutRoot") is Grid { ColumnDefinitions.Count: >= 2 } layoutRoot)
            {
                if (layoutRoot.ColumnDefinitions[0].Width != GridLength.Auto)
                    layoutRoot.ColumnDefinitions[0].Width = GridLength.Auto;
                GridLength glyphCol = new(GlyphColumnWidth);
                if (layoutRoot.ColumnDefinitions[1].Width != glyphCol)
                    layoutRoot.ColumnDefinitions[1].Width = glyphCol;
            }
        }
        finally
        {
            Applying.Remove(box);
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
