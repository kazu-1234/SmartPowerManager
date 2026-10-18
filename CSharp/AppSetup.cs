using WinUiShared;

namespace SmartPowerManager;

/// <summary>
/// このアプリの「型」と識別名（共通基盤 WinUiShared に渡す値はすべてここに集約する）。
/// SmartPowerManager は常駐型: トレイ常駐・ログオン自動起動・スリープ復帰時のスケジュール再評価を使う。
/// </summary>
internal static class AppSetup
{
    public static readonly AppProfile Profile = new()
    {
        // %AppData%\SmartPowerManager（AppPaths.AppDataDirectory と同じ場所）
        AppName = AppConstants.AppTitle,
        // 旧版と同じ Mutex / イベント名（Global\SmartPowerManager_SingleInstance_v2）にして、インストーラの --exit を届かせる
        InstanceNameVersion = "v2",
        LatestReleaseApiUrl = AppConstants.GitHubApiUrl,

        CloseBehavior = WindowCloseBehavior.KeepRunning,
        UseTray = true,
        UseResumeMonitor = true,
        // 終了時に自動起動 OFF なら残骸を消すのに使う（OFF の同期は登録方式によらず全部消す）
        SyncAutostart = enable => StartupManager.SyncAutostartWithSettings(enable),

        SystemEventWindowClassName = "SmartPowerManagerSystemEventWindow_v2",
        TrayWindowClassName = "SmartPowerManager_TrayMessageWindow_v1",
        TrayWindowTitle = "SmartPowerManagerTray",
        TrayIconFileName = "app_icon.ico",

        GetString = Strings.Get,
        FormatString = Strings.Format
    };
}
