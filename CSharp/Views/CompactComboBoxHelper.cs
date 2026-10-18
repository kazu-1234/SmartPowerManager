using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SmartPowerManager.Views;

/// <summary>
/// WinUI ComboBox 既定テンプレートでは内容列が * のため、文字と矢印のあいだに余白が開く。
/// 内容列を Auto にし、矢印列幅・Margin を詰めて文字の直後に矢印が来るようにする。
/// テーマ ComboBox など可変幅の場合は、Width を固定せず選択中テキストの自然な幅にする。
/// </summary>
internal static class CompactComboBoxHelper
{
    private const double GlyphColumnWidth = 20;
    private static readonly Thickness GlyphMargin = new(0, 0, 6, 0);
    private static readonly HashSet<ComboBox> FitToTextBoxes = [];
    private static readonly HashSet<ComboBox> Applying = [];

    // 閉じているときの実幅。ドロップダウンを開くと選択文字がポップアップ側へ移って
    // 自動幅の ComboBox が縮み、ポップアップ位置がずれるため、開いている間だけこの幅で固定する。
    private static readonly Dictionary<ComboBox, double> ClosedWidths = [];

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
        box.DropDownOpened += OnFitDropDownOpened;
        box.DropDownClosed += OnFitDropDownClosed;
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
        {
            // 開いている間の縮んだ幅は記録しない（閉じた状態の幅だけを固定用に覚える）
            if (!box.IsDropDownOpen)
                ClosedWidths[box] = e.NewSize.Width;
            Apply(box);
        }
    }

    private static void OnFitDropDownOpened(object? sender, object e)
    {
        if (sender is ComboBox box && ClosedWidths.TryGetValue(box, out double width) && width > 0)
            box.Width = width;
    }

    private static void OnFitDropDownClosed(object? sender, object e)
    {
        // 閉じたら固定を解除し、新しい選択文字の自然な幅に戻す
        if (sender is ComboBox box)
            FitWidthToSelectedText(box);
    }

    private static void OnFitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box)
            FitWidthToSelectedText(box);
    }

    private static void OnFitActualThemeChanged(FrameworkElement sender, object args)
    {
        if (sender is ComboBox box)
        {
            // テーマ切替でテンプレートの値が再評価されるので、列幅・余白を当て直して再計測させる
            Apply(box);
            box.InvalidateMeasure();
        }
    }

    private static void FitWidthToSelectedText(ComboBox box)
    {
        // 開いている間は幅固定中。選択変更はここで処理せず、DropDownClosed で合わせ直す
        if (box.IsDropDownOpen)
            return;

        // 幅は固定しない（Width を解除 = 自動）。Apply で内容列を Auto にしているため、
        // ComboBox は「選択中テキスト + 左右余白 + 矢印列」の自然な幅になる。
        // 以前はツリー外の TextBlock で文字幅を測って Width を固定していたが、
        // テーマ切替後の実寸とずれて「システムに従う」が見切れることがあった。
        box.ClearValue(FrameworkElement.WidthProperty);
        box.MinWidth = 0;
        box.ClearValue(FrameworkElement.MaxWidthProperty);

        Apply(box);
        box.InvalidateMeasure();
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
