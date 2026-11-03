using SmartPowerManager.Services;
using WinUiShared;

namespace SmartPowerManager;

/// <summary>
/// このアプリの「型」と識別名（共通基盤 WinUiShared に渡す値はすべてここに集約する）。
/// SmartPowerManager は常駐型: トレイ常駐・ログオン自動起動・スリープ復帰時のスケジュール再評価を使う。
/// </summary>
internal static class AppSetup
{
    /// <summary>
    /// 自動起動の登録名（タスク名・Run の名前。公開後は変えない）。
    /// ログオンタスク（既定）とスタートアップ（レジストリ Run）を Settings.UseLogonTask で切り替える。
    /// 登録・解除のたびに Python 版の Run と手作りのショートカットも消す（extraCleanup）。
    /// 起動時の移行は Python 版の Run からの移行だけを行う（customMigrate。既定の移行は行わない）。
    /// </summary>
    public static readonly AutostartController Autostart = new(
        new AutostartIdentity
        {
            AppName = AppConstants.AppTitle,
            LogonTaskName = AppConstants.AppTitle + " Logon",
            RegistryName = AppConstants.AppTitle,
            AllowRegistryRun = true
        },
        extraCleanup: ConfigMigrationService.RemoveLegacyAutostartArtifacts,
        customMigrate: MigrateFromPythonRegistry);

    /// <summary>
    /// Python 版の自動起動（レジストリ Run）が残っていれば、ログオンタスクへ移す。
    /// スタートアップ（Run）方式を選んでいるときは、選んだ Run を消さないよう何もしない（以前と同じ）。
    /// </summary>
    private static void MigrateFromPythonRegistry(bool autoStart, bool useLogonTask)
    {
        if (!useLogonTask || !ConfigMigrationService.IsPythonRegistryAutostartEnabled())
            return;

        // タスクがまだ無ければ作る（Sync は extraCleanup で Python 版の Run も消す）。あれば Run だけ消す
        if (!Autostart.IsEnabled())
            Autostart.Sync(true, useLogonTask: true);
        else
            ConfigMigrationService.RemovePythonRegistryAutostart();
    }

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
        // 自動起動の窓口。--sync-autostart / --cleanup-autostart、起動時の移行と同期、終了時の掃除を共通部が行う
        Autostart = Autostart,
        LoadSettings = Settings.Load,

        SystemEventWindowClassName = "SmartPowerManagerSystemEventWindow_v2",
        TrayWindowClassName = "SmartPowerManager_TrayMessageWindow_v1",
        TrayWindowTitle = "SmartPowerManagerTray",
        TrayIconFileName = "app_icon.ico",

        GetString = Strings.Get,
        FormatString = Strings.Format
    };
}
