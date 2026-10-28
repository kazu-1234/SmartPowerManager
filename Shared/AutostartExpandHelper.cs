using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinUiShared;

/// <summary>
/// 設定タブ表示時は自動起動 Expander を開いた状態で出し、初期展開アニメだけ打ち切る（SPM 正本）。
/// </summary>
public static class AutostartExpandHelper
{
    public static void AttachSkipInitialAnimation(SettingsExpander expander)
    {
        expander.Loaded += (_, _) =>
        {
            expander.IsExpanded = true;
            Skip(expander);
            expander.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Skip(expander));
        };
    }

    public static void Skip(SettingsExpander expander)
    {
        expander.IsExpanded = true;

        Expander? inner = FindDescendant<Expander>(expander);
        if (inner == null)
            return;

        inner.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(inner) > 0 &&
            VisualTreeHelper.GetChild(inner, 0) is FrameworkElement expanderRoot)
        {
            foreach (VisualStateGroup group in VisualStateManager.GetVisualStateGroups(expanderRoot))
            {
                foreach (VisualState state in group.States)
                    state.Storyboard?.SkipToFill();
            }
        }

        AnimatedIcon? chevron = FindDescendant<AnimatedIcon>(inner);
        if (chevron != null)
            AnimatedIcon.SetState(chevron, "NormalOn");
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            T? nested = FindDescendant<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }
}

/// <summary>自動起動の種類／パス表示（SPM 正本）。</summary>
public static class AutostartInfoFormatter
{
    public static string FormatTypeLine(
        bool enabled,
        bool useLogonTask,
        Func<string, string> getString,
        Func<string, object[], string> formatString)
    {
        string typeValue;
        if (!enabled)
            typeValue = getString("Settings_AutostartMode_Disabled");
        else if (useLogonTask)
            typeValue = getString("Settings_AutostartMode_TaskShort");
        else
            typeValue = getString("Settings_AutostartMode_StartupShort");

        return formatString("Settings_AutostartType_Format", new object[] { typeValue });
    }

    public static string FormatPathLine(
        bool enabled,
        string? registeredCommand,
        Func<string, string> getString,
        Func<string, object[], string> formatString)
    {
        string pathValue = enabled
            ? (string.IsNullOrWhiteSpace(registeredCommand)
                ? getString("Settings_Autostart_NotRegistered")
                : registeredCommand)
            : getString("Settings_Autostart_NotRegistered");
        return formatString("Settings_AutostartPath_Format", new object[] { pathValue });
    }
}
