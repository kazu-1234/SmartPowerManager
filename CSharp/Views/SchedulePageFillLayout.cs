using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SmartPowerManager.Views;

/// <summary>
/// スケジュール系ページのカード高さと、ページ縦方向の余白配置を揃える。
/// </summary>
internal static class SchedulePageFillLayout
{
    public static void Attach(
        Page page,
        FrameworkElement pageRoot,
        FrameworkElement leftCard,
        FrameworkElement rightCard,
        bool syncLeftAndRightHeights = true)
    {
        void Update()
        {
            ApplyCardHeights(leftCard, rightCard, syncLeftAndRightHeights);
            ApplyPageRootMinHeight(page, pageRoot);
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

        // 起動: 左は監視行がない分だけ低くし、各フォーム行の高さをシャットダウンと揃える。余白は下へ。
        double formRowHeight =
            (AppConstants.ScheduleCardHeight - AppConstants.ScheduleCardPaddingVertical) / 6.0;
        leftCard.Height = AppConstants.ScheduleCardPaddingVertical + formRowHeight * 5;
        leftCard.VerticalAlignment = VerticalAlignment.Top;
    }

    private static void ApplyPageRootMinHeight(Page page, FrameworkElement pageRoot)
    {
        double viewportHeight = FindScrollViewerViewportHeight(page);
        if (viewportHeight <= 0)
            viewportHeight = page.ActualHeight;

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
