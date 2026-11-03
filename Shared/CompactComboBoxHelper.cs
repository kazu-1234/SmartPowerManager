using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinUiShared;

/// <summary>
/// WinUI ComboBox 既定テンプレートでは内容列が * のため、文字と矢印のあいだに余白が開く。
/// 内容列を Auto にし、矢印列幅・Margin を詰めて文字の直後に矢印が来るようにする（SPM / BlueShift / Template 正本）。
/// テーマ ComboBox など可変幅の場合は、Width を固定せず選択中テキストの自然な幅にする。
/// </summary>
public static class CompactComboBoxHelper
{
    private const double GlyphColumnWidth = 20;
    private static readonly Thickness GlyphMargin = new(0, 0, 6, 0);

    /// <summary>枠線（左右 1px ずつ）の合計幅。幅を固定するときの計算に使う。</summary>
    private const double BorderThicknessTotal = 2;

    /// <summary>幅を固定する ComboBox の文字の左右余白（スタイル未適用時も 12px で測る。LeftHandDevice 由来）。</summary>
    private const double FixedSidePadding = 12;

    /// <summary>標準テンプレートの右端の ∨（ドロップダウンの印）が使う幅（列 32 + 余白。MusicPlayer 由来）。</summary>
    private const double DefaultGlyphAreaWidth = 38;

    /// <summary>
    /// 共通ラベル集合ごとの固定幅のキャッシュ（同じ選択肢を持つ ComboBox を何個並べても同じ幅にそろえるため）。
    /// キーは文字列だけなので、ComboBox を保持し続けることはない。
    /// </summary>
    private static readonly Dictionary<string, double> SharedWidthCache = [];

    /// <summary>ComboBox ごとの付加情報。</summary>
    private sealed class BoxState
    {
        public bool IsCompactAttached;
        public bool FitToText;
        public bool GlyphHidden;
        public bool Applying;

        // 外枠の幅を固定する ComboBox（最長の項目／共通ラベル集合に合わせる）。内側の文字列は * 列にする
        public bool FixedWidth;
        public IReadOnlyList<string>? SharedLabels;

        // 閉じているときの実幅。ドロップダウンを開くと選択文字がポップアップ側へ移って
        // 自動幅の ComboBox が縮み、ポップアップ位置がずれるため、開いている間だけこの幅で固定する。
        public double ClosedWidth;
    }

    // ConditionalWeakTable は ComboBox が GC されると対応する状態も一緒に消える。
    // static な HashSet で持つとページを開くたびに ComboBox（とページ全体）が残り続けるため、これを使う。
    private static readonly ConditionalWeakTable<ComboBox, BoxState> States = new();

    private static BoxState GetState(ComboBox box) => States.GetValue(box, _ => new BoxState());

    public static void Attach(params ComboBox[] boxes)
    {
        foreach (ComboBox box in boxes)
        {
            BoxState state = GetState(box);
            // 同じ ComboBox に二重にイベントを付けない
            if (state.IsCompactAttached)
                continue;
            state.IsCompactAttached = true;

            if (box.IsLoaded)
                Apply(box);
            box.Loaded += OnLoaded;
            box.SizeChanged += OnSizeChanged;
            box.SelectionChanged += OnSelectionChanged;
        }
    }

    /// <summary>
    /// ドロップダウン矢印を消し、内容列を全幅にする（時分コンボなど用）。
    /// </summary>
    public static void HideDropDownGlyph(ComboBox box)
    {
        BoxState state = GetState(box);
        if (!state.GlyphHidden)
        {
            state.GlyphHidden = true;
            box.Loaded += OnHideGlyphLoaded;
            box.SizeChanged += OnHideGlyphSizeChanged;
        }

        ApplyHideDropDownGlyph(box);
    }

    /// <summary>
    /// 選択中の文言に合わせて ComboBox 幅を伸縮させる（テーマ ComboBox 用）。
    /// </summary>
    public static void AttachFitToSelectedText(ComboBox box)
    {
        BoxState state = GetState(box);
        if (state.FitToText)
            return;
        state.FitToText = true;

        box.Loaded += OnFitLoaded;
        box.SizeChanged += OnFitSizeChanged;
        box.SelectionChanged += OnFitSelectionChanged;
        box.ActualThemeChanged += OnFitActualThemeChanged;
        box.DropDownOpened += OnFitDropDownOpened;
        box.DropDownClosed += OnFitDropDownClosed;
        if (box.IsLoaded)
            FitWidthToSelectedText(box);
    }

    /// <summary>選択文言に合わせる ComboBox の幅を、今の選択でもう一度合わせ直す（項目を入れ替えたあとなど）。</summary>
    public static void RefreshFitToSelectedText(ComboBox box)
    {
        if (GetState(box).FitToText)
            FitWidthToSelectedText(box);
    }

    /// <summary>
    /// 指定ラベル集合のうち最長の文字列で外枠を固定する。選択中の文字では縮めない
    /// （同じ選択肢の ComboBox を縦に並べたとき、幅がそろうようにする。LeftHandDevice の手順の種類など）。
    /// </summary>
    public static void AttachFixedToSharedLabels(ComboBox box, IReadOnlyList<string> labels)
    {
        BoxState state = GetState(box);
        state.FixedWidth = true;
        bool firstAttach = state.SharedLabels == null;
        state.SharedLabels = labels;
        LockSharedOuterWidth(box, labels);
        if (!firstAttach)
            return;

        box.Loaded += OnSharedChanged;
        box.SizeChanged += OnSharedChanged;
        box.SelectionChanged += OnSharedChanged;
        box.ActualThemeChanged += OnSharedThemeChanged;
    }

    /// <summary>項目のうち最も長い文字列で外枠の幅を固定する。選択変更では縮めない。</summary>
    public static void AttachFixedToLongestItem(ComboBox box)
    {
        BoxState state = GetState(box);
        bool firstAttach = !state.FixedWidth;
        state.FixedWidth = true;
        LockLongestItemOuterWidth(box);
        if (!firstAttach)
            return;

        box.Loaded += OnFixedChanged;
        box.SizeChanged += OnFixedChanged;
        box.SelectionChanged += OnFixedChanged;
        box.ActualThemeChanged += OnFixedThemeChanged;
    }

    /// <summary>最長の項目で固定している ComboBox の幅を、今の項目で計り直す（項目を入れ替えたあと）。</summary>
    public static void RefreshFixedWidth(ComboBox box)
    {
        if (GetState(box).FixedWidth)
            LockLongestItemOuterWidth(box);
    }

    /// <summary>
    /// 標準テンプレートのまま使う ComboBox の最小幅を「一番長い項目」に合わせる（MusicPlayer の設定画面など）。
    /// ボックスが項目より狭いと、開いたリストの方が幅広になり、ボックスからはみ出して横にずれて見えるため、
    /// 最初から同じ幅にしておく。項目を入れ終えた後に呼ぶ。
    /// </summary>
    public static void FitWidthToWidestItem(ComboBox box)
    {
        double widest = 0;
        foreach (object item in box.Items)
            widest = Math.Max(widest, MeasureTextWidth(box, GetItemDisplayText(item)));

        // 文字の左右の余白（Padding）＋ 右端の ∨ の列 ＋ 枠線 の分を足す
        box.MinWidth = Math.Ceiling(widest + box.Padding.Left + box.Padding.Right + DefaultGlyphAreaWidth + BorderThicknessTotal);
    }

    private static void OnSharedChanged(object sender, object e)
    {
        if (sender is ComboBox box && GetState(box).SharedLabels is { } labels)
            LockSharedOuterWidth(box, labels);
    }

    private static void OnSharedThemeChanged(FrameworkElement sender, object args)
    {
        // テーマが変わると文字の測り方（フォント）が変わり得るので、キャッシュを捨てて計り直す
        if (sender is ComboBox box && GetState(box).SharedLabels is { } labels)
        {
            SharedWidthCache.Remove(SharedKey(labels));
            LockSharedOuterWidth(box, labels);
        }
    }

    private static void OnFixedChanged(object sender, object e)
    {
        if (sender is ComboBox box && GetState(box).SharedLabels == null)
            LockLongestItemOuterWidth(box);
    }

    private static void OnFixedThemeChanged(FrameworkElement sender, object args) => OnFixedChanged(sender, args);

    /// <summary>ラベル集合を 1 つの文字列キーにする（区切りは通常の文言に出ない制御文字）。</summary>
    private static string SharedKey(IReadOnlyList<string> labels) => string.Join("\u001f", labels);

    private static void LockSharedOuterWidth(ComboBox box, IReadOnlyList<string> labels)
    {
        string key = SharedKey(labels);
        if (!SharedWidthCache.TryGetValue(key, out double width))
        {
            double maxContent = 0;
            foreach (string label in labels)
            {
                if (!string.IsNullOrEmpty(label))
                    maxContent = Math.Max(maxContent, MeasureTextWidth(box, label));
            }

            if (maxContent <= 0)
                return;

            width = ComputeLockedWidth(maxContent);
            SharedWidthCache[key] = width;
        }

        ApplyLockedOuterWidth(box, width);
    }

    private static void LockLongestItemOuterWidth(ComboBox box)
    {
        double maxContent = 0;
        foreach (object item in box.Items)
        {
            string text = GetItemDisplayText(item);
            if (!string.IsNullOrEmpty(text))
                maxContent = Math.Max(maxContent, MeasureTextWidth(box, text));
        }

        if (maxContent <= 0)
            return;

        ApplyLockedOuterWidth(box, ComputeLockedWidth(maxContent));
    }

    /// <summary>文字幅 + 左右余白 + 矢印列 + 矢印の右余白 + 枠線 = 外枠の幅（小数は切り上げ）。</summary>
    private static double ComputeLockedWidth(double contentWidth) =>
        Math.Ceiling(contentWidth + FixedSidePadding + FixedSidePadding + GlyphColumnWidth + GlyphMargin.Right + BorderThicknessTotal);

    private static void ApplyLockedOuterWidth(ComboBox box, double newWidth)
    {
        // 0.5px 未満の差は書き換えない（書き換えると SizeChanged → 再計算の繰り返しになる）
        if (double.IsNaN(box.Width) || Math.Abs(box.Width - newWidth) > 0.5)
            box.Width = newWidth;
        if (Math.Abs(box.MinWidth - newWidth) > 0.5)
            box.MinWidth = newWidth;
        if (Math.Abs(box.MaxWidth - newWidth) > 0.5)
            box.MaxWidth = newWidth;

        ApplyFixedInnerLayout(box);
    }

    /// <summary>外枠は固定幅。内側の文字列の列は * にして、短い選択でも枠が縮まないようにする。</summary>
    private static void ApplyFixedInnerLayout(ComboBox box)
    {
        BoxState state = GetState(box);
        if (state.Applying)
            return;

        state.Applying = true;
        try
        {
            if (FindDescendant(box, "LayoutRoot") == null)
                box.ApplyTemplate();

            if (FindDescendant(box, "DropDownGlyph") is FrameworkElement glyph)
                glyph.Margin = GlyphMargin;

            if (FindDescendant(box, "ContentPresenter") is FrameworkElement content)
            {
                content.HorizontalAlignment = HorizontalAlignment.Left;
                content.Margin = new Thickness(FixedSidePadding, box.Padding.Top, FixedSidePadding, box.Padding.Bottom);
            }

            if (FindDescendant(box, "LayoutRoot") is Grid { ColumnDefinitions.Count: >= 2 } layoutRoot)
            {
                GridLength star = new(1, GridUnitType.Star);
                if (layoutRoot.ColumnDefinitions[0].Width != star)
                    layoutRoot.ColumnDefinitions[0].Width = star;
                GridLength glyphCol = new(GlyphColumnWidth);
                if (layoutRoot.ColumnDefinitions[1].Width != glyphCol)
                    layoutRoot.ColumnDefinitions[1].Width = glyphCol;
            }
        }
        finally
        {
            state.Applying = false;
        }
    }

    private static string GetItemDisplayText(object? item) =>
        item is ComboBoxItem cbi ? cbi.Content?.ToString() ?? string.Empty : item?.ToString() ?? string.Empty;

    /// <summary>ツリーの外の TextBlock で、その ComboBox のフォントで文字幅を測る。</summary>
    private static double MeasureTextWidth(ComboBox box, string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var measureBlock = new TextBlock
        {
            Text = text,
            FontSize = box.FontSize,
            FontFamily = box.FontFamily,
            FontWeight = box.FontWeight,
            FontStyle = box.FontStyle,
            FontStretch = box.FontStretch
        };
        measureBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return measureBlock.DesiredSize.Width;
    }

    private static void OnHideGlyphLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox box)
            ApplyHideDropDownGlyph(box);
    }

    private static void OnHideGlyphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ComboBox box)
            ApplyHideDropDownGlyph(box);
    }

    private static void ApplyHideDropDownGlyph(ComboBox box)
    {
        if (FindDescendant(box, "LayoutRoot") == null)
            box.ApplyTemplate();

        if (FindDescendant(box, "DropDownGlyph") is FrameworkElement glyph)
            glyph.Visibility = Visibility.Collapsed;

        if (FindDescendant(box, "LayoutRoot") is Grid { ColumnDefinitions.Count: >= 2 } layoutRoot)
        {
            layoutRoot.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            layoutRoot.ColumnDefinitions[1].Width = new GridLength(0);
            layoutRoot.MinHeight = 0;
            if (!double.IsNaN(box.Height) && box.Height > 0)
            {
                layoutRoot.Height = box.Height;
                layoutRoot.MaxHeight = box.Height;
            }
        }

        if (FindDescendant(box, "ContentPresenter") is FrameworkElement content)
        {
            // Padding は ComboBox 側。Margin を足すと左寄りに見えるため解除して中央揃え。
            content.Margin = new Thickness(0);
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (content is ContentPresenter presenter)
                presenter.HorizontalContentAlignment = HorizontalAlignment.Center;
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
                GetState(box).ClosedWidth = e.NewSize.Width;
            Apply(box);
        }
    }

    private static void OnFitDropDownOpened(object? sender, object e)
    {
        if (sender is ComboBox box && GetState(box).ClosedWidth is > 0 and var width)
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
        // ツリー外の TextBlock で文字幅を測って Width を固定すると、
        // テーマ切替後の実寸とずれて「システムの設定に従う」が見切れることがあった。
        box.ClearValue(FrameworkElement.WidthProperty);
        box.MinWidth = 0;
        box.ClearValue(FrameworkElement.MaxWidthProperty);

        Apply(box);
        box.InvalidateMeasure();
    }

    private static void Apply(ComboBox box)
    {
        BoxState state = GetState(box);

        // 幅を固定している ComboBox は、内容列を Auto にせず * のまま（固定幅の内側で左寄せ）
        if (state.FixedWidth)
        {
            ApplyFixedInnerLayout(box);
            return;
        }

        // Apply 中のレイアウト変更で SizeChanged が再入するのを防ぐ
        if (state.Applying)
            return;

        state.Applying = true;
        try
        {
            // ApplyTemplate は視覚ツリーを作り直して切替時に点滅する。未展開時のみ。
            if (FindDescendant(box, "LayoutRoot") == null)
                box.ApplyTemplate();

            if (FindDescendant(box, "DropDownGlyph") is FrameworkElement glyph)
                glyph.Margin = GlyphMargin;

            if (state.FitToText
                && FindDescendant(box, "ContentPresenter") is FrameworkElement content)
            {
                // 文字の左右余白をそろえる（Padding.Left を右側にも使う）
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
            state.Applying = false;
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
