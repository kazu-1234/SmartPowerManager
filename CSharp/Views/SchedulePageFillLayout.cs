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
    /// <summary>タイトルとカードの間隔。</summary>
    private const double TitleAboveCardsGap = 12;

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
            PositionTitleAboveCards(pageTitle, content);
        }

        Update();
        page.Loaded += (_, _) => Update();
        page.SizeChanged += (_, _) => Update();
        pageRoot.SizeChanged += (_, _) => Update();
        content.SizeChanged += (_, _) => PositionTitleAboveCards(pageTitle, content);
        pageTitle.SizeChanged += (_, _) => PositionTitleAboveCards(pageTitle, content);
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

    /// <summary>
    /// レイアウト上のカード位置は変えず、タイトルだけカード直上へ描画する。
    /// </summary>
    private static void PositionTitleAboveCards(FrameworkElement title, FrameworkElement content)
    {
        if (title.ActualHeight <= 0)
            title.UpdateLayout();

        double titleHeight = title.ActualHeight;
        if (titleHeight <= 0)
            titleHeight = title.DesiredSize.Height;
        if (titleHeight <= 0)
            return;

        if (title.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            title.RenderTransform = transform;
        }

        // カード上端からタイトル高さ+余白ぶん上へ（レイアウトには影響しない）
        transform.Y = -(titleHeight + TitleAboveCardsGap);
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
