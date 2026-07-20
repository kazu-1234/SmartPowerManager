using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SmartPowerManager.Views;

/// <summary>
/// スケジュール系ページのカード高さのみ揃える。
/// 縦位置は XAML の * / Auto / * 行で固定し、コードで Margin を動かさない（再描画ジャンプ防止）。
/// </summary>
internal static class SchedulePageFillLayout
{
    public static void Attach(
        Page page,
        FrameworkElement pageRoot,
        FrameworkElement pageTitle,
        FrameworkElement content,
        FrameworkElement leftCard,
        FrameworkElement rightCard,
        bool syncLeftAndRightHeights = true)
    {
        void Update()
        {
            ApplyCardHeights(leftCard, rightCard, syncLeftAndRightHeights);
            ApplyPageRootMinHeight(pageRoot);
        }

        Update();
        page.Loaded += (_, _) => Update();
        page.SizeChanged += (_, _) => Update();
        pageRoot.SizeChanged += (_, _) => Update();
    }

    public static void ApplyCardHeights(
        FrameworkElement leftCard,
        FrameworkElement rightCard,
        bool syncLeftAndRightHeights = true)
    {
        double height = AppConstants.ScheduleCardHeight;
        rightCard.Height = height;
        rightCard.VerticalAlignment = VerticalAlignment.Top;

        if (syncLeftAndRightHeights)
        {
            leftCard.Height = height;
            leftCard.VerticalAlignment = VerticalAlignment.Top;
            return;
        }

        double formRowHeight =
            (AppConstants.ScheduleCardHeight - AppConstants.ScheduleCardPaddingVertical) / 6.0;
        leftCard.Height = AppConstants.ScheduleCardPaddingVertical + formRowHeight * 5;
        leftCard.VerticalAlignment = VerticalAlignment.Top;
    }

    private static void ApplyPageRootMinHeight(FrameworkElement pageRoot)
    {
        double viewportHeight = FindScrollViewerViewportHeight(pageRoot);
        if (viewportHeight <= 0)
            viewportHeight = pageRoot.ActualHeight;

        if (viewportHeight > 0)
            pageRoot.MinHeight = viewportHeight;
    }

    private static double FindScrollViewerViewportHeight(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is ScrollViewer sv && sv.ViewportHeight > 0)
                return sv.ViewportHeight;

            current = VisualTreeHelper.GetParent(current);
        }

        return 0;
    }
}
