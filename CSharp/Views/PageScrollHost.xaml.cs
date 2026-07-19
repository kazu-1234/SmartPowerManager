using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SmartPowerManager.Views
{
    public sealed partial class PageScrollHost : ContentControl
    {
        private ScrollViewer? _scrollViewer;
        private ContentPresenter? _contentPresenter;
        private FrameworkElement? _contentRoot;
        private bool _scrollEnabled;
        private bool _updateScheduled;

        public PageScrollHost()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, __) => ScheduleUpdateScrollability();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _scrollViewer = GetTemplateChild("PART_ScrollViewer") as ScrollViewer;
            _contentPresenter = GetTemplateChild("PART_ContentPresenter") as ContentPresenter;
            if (_scrollViewer != null)
            {
                _scrollViewer.SizeChanged += (_, __) => ScheduleUpdateScrollability();
                _scrollViewer.PointerWheelChanged += ScrollViewer_PointerWheelChanged;
            }

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

            _contentRoot = root;
            root.SizeChanged += ContentRoot_SizeChanged;
            root.Loaded += ContentRoot_Loaded;
        }

        private void UnwatchContentRoot(FrameworkElement root)
        {
            root.SizeChanged -= ContentRoot_SizeChanged;
            root.Loaded -= ContentRoot_Loaded;

            if (ReferenceEquals(_contentRoot, root))
                _contentRoot = null;
        }

        private void ContentRoot_Loaded(object sender, RoutedEventArgs e)
        {
            ScheduleUpdateScrollability();
        }

        private void ContentRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleUpdateScrollability();
        }

        private void ScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // 見切れていないときはホイールでもスクロールしない
            if (!_scrollEnabled)
                e.Handled = true;
        }

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

            bool needsScroll = ComputeNeedsScroll();
            ApplyScrollState(needsScroll);

            if (!needsScroll && _scrollViewer.VerticalOffset > 0)
                _scrollViewer.ChangeView(null, 0, null, disableAnimation: true);

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_scrollViewer == null)
                    return;

                bool needsScrollAfterLayout = ComputeNeedsScroll();
                ApplyScrollState(needsScrollAfterLayout);

                if (!needsScrollAfterLayout && _scrollViewer.VerticalOffset > 0)
                    _scrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            });
        }

        private bool ComputeNeedsScroll()
        {
            if (_scrollViewer == null)
                return false;

            double viewportHeight = _scrollViewer.ViewportHeight;
            if (viewportHeight <= 0)
                return false;

            double contentHeight = MeasureNaturalContentHeight(viewportHeight);
            return contentHeight > viewportHeight + 1.0;
        }

        /// <summary>
        /// MinHeight でビューポートに引き伸ばしているだけの余白は、スクロール判定に使わない。
        /// </summary>
        private double MeasureNaturalContentHeight(double viewportHeight)
        {
            if (_contentRoot == null)
                return _scrollViewer?.ExtentHeight ?? 0;

            double width = _contentRoot.ActualWidth;
            if (width <= 0)
                width = _scrollViewer?.ViewportWidth ?? 0;
            if (width <= 0)
                return _contentRoot.ActualHeight;

            double savedMinHeight = _contentRoot.MinHeight;
            try
            {
                // 一時的に MinHeight を外して本来の必要高さを測る
                _contentRoot.MinHeight = 0;
                _contentRoot.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
                double natural = _contentRoot.DesiredSize.Height;
                if (natural > 0)
                    return natural;
            }
            finally
            {
                _contentRoot.MinHeight = savedMinHeight;
            }

            double actual = _contentRoot.ActualHeight;
            // MinHeight で伸ばされているだけの場合はスクロール不要
            if (savedMinHeight > 0 && actual <= savedMinHeight + 1.0 && savedMinHeight >= viewportHeight - 1.0)
                return Math.Min(actual, viewportHeight);

            return actual;
        }

        private void ApplyScrollState(bool enabled)
        {
            if (_scrollViewer == null)
                return;

            _scrollEnabled = enabled;
            _scrollViewer.VerticalScrollMode = enabled ? ScrollMode.Enabled : ScrollMode.Disabled;
            _scrollViewer.VerticalScrollBarVisibility = enabled
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Hidden;
            _scrollViewer.VerticalContentAlignment = VerticalAlignment.Top;

            if (_contentPresenter != null)
                _contentPresenter.VerticalAlignment = VerticalAlignment.Top;
        }
    }
}
