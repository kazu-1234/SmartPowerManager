using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace SmartPowerManager.Views;

public sealed partial class InfoPage : Page
{
    public InfoPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        VersionText.Text = Strings.Format("Settings_CurrentVersion", UpdateChecker.CurrentVersion);
    }
}
