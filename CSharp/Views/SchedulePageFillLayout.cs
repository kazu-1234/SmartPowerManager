using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SmartPowerManager.Views;

/// <summary>
/// スケジュール系ページのカード高さのみ揃える。
/// 縦位置は XAML の上寄せレイアウトに任せ、コードで Margin を動かさない。
/// </summary>
internal static class SchedulePageFillLayout
{
    public static void Attach(
        Page page,
        FrameworkElement leftCard,
        FrameworkElement rightCard,
        bool syncLeftAndRightHeights = true)
    {
        void Update() => ApplyCardHeights(leftCard, rightCard, syncLeftAndRightHeights);

        Update();
        page.Loaded += (_, _) => Update();
        page.SizeChanged += (_, _) => Update();
    }

    public static void ApplyCardHeights(
        FrameworkElement leftCard,
        FrameworkElement rightCard,
        bool syncLeftAndRightHeights = true)
    {
        double height = AppConstants.ScheduleCardHeight;

        rightCard.ClearValue(FrameworkElement.HeightProperty);
        rightCard.VerticalAlignment = VerticalAlignment.Top;

        leftCard.ClearValue(FrameworkElement.HeightProperty);
        leftCard.VerticalAlignment = VerticalAlignment.Top;

        if (syncLeftAndRightHeights)
        {
            // 左右とも ScheduleCardHeight のみ（空の +40 は付けない）
            leftCard.MinHeight = height;
            rightCard.MinHeight = height;
            rightCard.Height = height;
            return;
        }

        rightCard.Height = height;
        const double onetimeExtraHeight = 40;
        double formRowHeight =
            (AppConstants.ScheduleCardHeight - AppConstants.ScheduleCardPaddingVertical) / 6.0;
        leftCard.MinHeight =
            AppConstants.ScheduleCardPaddingVertical + formRowHeight * 5 + onetimeExtraHeight;
    }
}
