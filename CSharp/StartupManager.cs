using SmartPowerManager.Services;
using WinUiShared;

namespace SmartPowerManager;

/// <summary>自動起動のアプリ側入口。実体は WinUiShared.AutostartService（SPM 正本）。</summary>
public static class StartupManager
{
    private static readonly AutostartIdentity Identity = new()
    {
        AppName = "SmartPowerManager",
        LogonTaskName = "SmartPowerManager Logon",
        RegistryName = "SmartPowerManager",
        AllowRegistryRun = true
    };

    public static bool IsAutoStartEnabled() => AutostartService.IsEnabled(Identity);

    public static bool SyncAutostartWithSettings(bool enable, bool useLogonTask = true) =>
        AutostartService.Sync(
            Identity,
            enable,
            useLogonTask,
            extraCleanup: ConfigMigrationService.RemoveLegacyAutostartArtifacts);

    public static bool ApplyAutoStart(bool enable, bool useLogonTask = true) =>
        SyncAutostartWithSettings(enable, useLogonTask);

    public static void ValidateAutoStart(bool autoStartEnabled, bool useLogonTask = true) =>
        SyncAutostartWithSettings(autoStartEnabled, useLogonTask);

    public static string? GetRegisteredCommand(bool preferLogonTask = true) =>
        AutostartService.GetRegisteredCommand(Identity, preferLogonTask);

    public static void MigrateFromPythonRegistryIfNeeded()
    {
        if (!ConfigMigrationService.IsPythonRegistryAutostartEnabled())
            return;

        if (!IsAutoStartEnabled())
            SyncAutostartWithSettings(true, useLogonTask: true);
        else
            ConfigMigrationService.RemovePythonRegistryAutostart();
    }

    public static void CleanupAutostartOnly() => SyncAutostartWithSettings(false);
}
