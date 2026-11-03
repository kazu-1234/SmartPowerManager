using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUiShared;

namespace SmartPowerManager.Views
{
    /// <summary>
    /// 情報ページ。「更新の確認」欄の処理は共通部の UpdateInfoController が行う（XAML の部品を渡すだけ）。
    /// </summary>
    public sealed partial class InfoPage : Page
    {
        private readonly UpdateInfoController _update;

        public InfoPage()
        {
            InitializeComponent();
            _update = new UpdateInfoController(new UpdateInfoControls
            {
                VersionText = VersionText,
                LastUpdateCheckText = LastUpdateCheckText,
                CheckUpdateButton = CheckUpdateButton,
                UpdateInfoBar = UpdateInfoBar,
                AutoUpdateToggleCard = AutoUpdateToggleCard,
                AutoUpdateToggle = AutoUpdateToggle,
                InstallUpdateCard = InstallUpdateCard,
                InstallStatusText = InstallStatusText,
                InstallUpdateButton = InstallUpdateButton
            });
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _update.Bind((e.Parameter as AppState)?.Settings);
        }
    }
}
