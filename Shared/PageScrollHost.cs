using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace WinUiShared;

/// <summary>
/// ページ全体を包むスクロール枠。中身が画面に収まるときはスクロール（ホイール・バー）を止め、
/// はみ出すときだけスクロールできるようにする（全アプリ共通。SPM 版が正本）。
///
/// 使い方（各ページの XAML）:
///   xmlns:shared="using:WinUiShared"
///   &lt;shared:PageScrollHost&gt; ページの中身 &lt;/shared:PageScrollHost&gt;
///
/// ScrollViewer の見た目は各アプリ App.xaml の <c>PageHostScrollViewerStyle</c> を使う（無ければ既定のまま）。
/// </summary>
public sealed class PageScrollHost : ContentControl
{
    /// <summary>各アプリの App.xaml にある ScrollViewer 用スタイルのキー。</summary>
    private const string ScrollViewerStyleKey = "PageHostScrollViewerStyle";

    /// <summary>中身の高さが表示高さをこの値（px）より超えたらスクロールありとみなす（小数の誤差で揺れないように）。</summary>
    private const double ScrollThreshold = 0.5;

    // ライブラリには .xaml を置かず、テンプレートは文字列から作る。
    // TargetType を ContentControl にして、このアセンブリの名前空間を XAML 側で参照しなくて済むようにしている。
    private const string TemplateXaml =
        "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "                 xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"ContentControl\">" +
        "  <ScrollViewer x:Name=\"PART_ScrollViewer\">" +
        "    <ContentPresenter HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Top\" HorizontalContentAlignment=\"Stretch\"" +
        "                      Content=\"{TemplateBinding Content}\" ContentTemplate=\"{TemplateBinding ContentTemplate}\" />" +
        "  </ScrollViewer>" +
        "</ControlTemplate>";

    private ScrollViewer? _scrollViewer;
    private FrameworkElement? _contentRoot;
    private bool _scrollEnabled;
    private bool _updateScheduled;

    public PageScrollHost()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Template = (ControlTemplate)XamlReader.Load(TemplateXaml);

        Loaded += OnLoaded;
        SizeChanged += (_, __) => ScheduleUpdateScrollability();
    }

    /// <summary>テンプレート適用時（1 回）に ScrollViewer を取り出し、スタイルとイベントを付ける。</summary>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_scrollViewer != null)
            _scrollViewer.PointerWheelChanged -= ScrollViewer_PointerWheelChanged;

        _scrollViewer = GetTemplateChild("PART_ScrollViewer") as ScrollViewer;
        if (_scrollViewer == null)
            return;

        // 文字列テンプレートでは StaticResource が引けないので、アプリのリソースからコードで当てる
        if (Application.Current?.Resources.TryGetValue(ScrollViewerStyleKey, out object? style) == true
            && style is Style scrollViewerStyle)
        {
            _scrollViewer.Style = scrollViewerStyle;
        }

        _scrollViewer.SizeChanged += (_, __) => ScheduleUpdateScrollability();
        _scrollViewer.PointerWheelChanged += ScrollViewer_PointerWheelChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WatchContentRoot(Content as FrameworkElement);
        ScheduleUpdateScrollability();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (oldContent is FrameworkElement oldRoot)
            UnwatchContentRoot(oldRoot);

        WatchContentRoot(newContent as FrameworkElement);
        ScheduleUpdateScrollability();
    }

    private void WatchContentRoot(FrameworkElement? root)
    {
        if (root == null)
            return;

        // 二重登録を防ぐ（ページを開き直すと Loaded が再び来るため、いったん外してから付ける）
        UnwatchContentRoot(root);

        _contentRoot = root;
        root.SizeChanged += ContentRoot_SizeChanged;
        root.Loaded += ContentRoot_Loaded;
        // SettingsExpander 展開後など、SizeChanged だけでは足りない更新を拾う
        root.LayoutUpdated += ContentRoot_LayoutUpdated;
    }

    private void UnwatchContentRoot(FrameworkElement root)
    {
        root.SizeChanged -= ContentRoot_SizeChanged;
        root.Loaded -= ContentRoot_Loaded;
        root.LayoutUpdated -= ContentRoot_LayoutUpdated;

        if (ReferenceEquals(_contentRoot, root))
            _contentRoot = null;
    }

    private void ContentRoot_Loaded(object sender, RoutedEventArgs e) => ScheduleUpdateScrollability();

    private void ContentRoot_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleUpdateScrollability();

    private void ContentRoot_LayoutUpdated(object? sender, object e) => ScheduleUpdateScrollability();

    /// <summary>スクロール不要なときはホイールを握りつぶし、ページが少しだけ動くのを防ぐ。</summary>
    private void ScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrollEnabled)
            e.Handled = true;
    }

    /// <summary>
    /// 判定は低優先度で 1 回にまとめる（LayoutUpdated は頻繁に来るので、毎回すぐ計算すると重くなる）。
    /// </summary>
    private void ScheduleUpdateScrollability()
    {
        if (_updateScheduled)
            return;

        _updateScheduled = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _updateScheduled = false;
            UpdateScrollability();
        });
    }

    private void UpdateScrollability()
    {
        if (_scrollViewer == null)
            return;

        _scrollViewer.UpdateLayout();
        _contentRoot?.UpdateLayout();

        ApplyAndResetOffset(ComputeNeedsScroll());

        // レイアウト確定後にもう一度判定する（直後はサイズが古いことがある）
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_scrollViewer != null)
                ApplyAndResetOffset(ComputeNeedsScroll());
        });
    }

    /// <summary>スクロール可否を反映し、不要になったときは先頭まで戻す（途中で止まったままにしない）。</summary>
    private void ApplyAndResetOffset(bool needsScroll)
    {
        if (_scrollViewer == null)
            return;

        ApplyScrollState(needsScroll);

        if (!needsScroll && _scrollViewer.VerticalOffset > 0)
            _scrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    private bool ComputeNeedsScroll()
    {
        if (_scrollViewer == null)
            return false;

        double viewportHeight = _scrollViewer.ViewportHeight;
        if (viewportHeight <= 0)
            return false;

        double contentHeight = 0;
        if (_contentRoot != null)
        {
            // ScrollMode=Disabled だと子に viewport 高が渡り ActualHeight が潰れる。
            // 無限高さで測り DesiredSize（本来ほしい高さ）を使う。
            double width = _scrollViewer.ViewportWidth;
            if (width <= 0)
                width = _contentRoot.ActualWidth;
            if (width <= 0)
                width = ActualWidth;

            if (width > 0)
            {
                _contentRoot.Measure(new Size(width, double.PositiveInfinity));
                contentHeight = _contentRoot.DesiredSize.Height;
            }

            if (contentHeight <= 0)
                contentHeight = _contentRoot.ActualHeight;
        }

        if (contentHeight <= 0)
            contentHeight = _scrollViewer.ExtentHeight;

        return contentHeight > viewportHeight + ScrollThreshold;
    }

    private void ApplyScrollState(bool enabled)
    {
        if (_scrollViewer == null)
            return;

        _scrollEnabled = enabled;
        _scrollViewer.VerticalScrollMode = enabled ? ScrollMode.Auto : ScrollMode.Disabled;
        _scrollViewer.VerticalScrollBarVisibility = enabled
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
    }
}
