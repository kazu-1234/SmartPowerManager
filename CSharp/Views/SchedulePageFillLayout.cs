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
        rightCard.Height = height;
        rightCard.VerticalAlignment = VerticalAlignment.Top;

        // 一回限り二段分の余裕を MinHeight に含め、行の見切れを防ぐ
        const double onetimeExtraHeight = 40;
        leftCard.ClearValue(FrameworkElement.HeightProperty);
        leftCard.VerticalAlignment = VerticalAlignment.Top;

        if (syncLeftAndRightHeights)
        {
            leftCard.MinHeight = height + onetimeExtraHeight;
            return;
        }

        double formRowHeight =
            (AppConstants.ScheduleCardHeight - AppConstants.ScheduleCardPaddingVertical) / 6.0;
        leftCard.MinHeight =
            AppConstants.ScheduleCardPaddingVertical + formRowHeight * 5 + onetimeExtraHeight;
    }
}
