using Microsoft.Win32;

namespace SmartPowerManager.Services;

public static class ConfigMigrationService
{
    public static void MigrateIfNeeded()
    {
        string? dir = Path.GetDirectoryName(AppPaths.SchedulesFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(AppPaths.SchedulesFilePath))
            return;

        if (File.Exists(AppPaths.LegacySchedulesFilePath))
        {
            File.Copy(AppPaths.LegacySchedulesFilePath, AppPaths.SchedulesFilePath);
            return;
        }

        string parentLegacy = Path.Combine(Directory.GetParent(AppPaths.ExecutableDirectory)?.FullName ?? string.Empty, "schedules.json");
        if (!string.IsNullOrEmpty(parentLegacy) && File.Exists(parentLegacy))
            File.Copy(parentLegacy, AppPaths.SchedulesFilePath);
    }

    public static void RunStartupCleanup()
    {
        CleanManualStartupShortcuts();
        CleanOldUpdateFiles();
        CleanupLegacyBat();
    }

    private static void CleanManualStartupShortcuts()
    {
        try
        {
            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string startupPath = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\Startup");
            string[] targets =
            [
                "SmartPowerManager.lnk",
                "SmartPowerManager - Shortcut.lnk"
            ];

            foreach (string name in targets)
            {
                string path = Path.Combine(startupPath, name);
                if (File.Exists(path))
                    File.Delete(path);
            }

            foreach (string path in Directory.EnumerateFiles(startupPath, "SmartPowerManager_v*.lnk"))
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch
        {
        }
    }

    private static void CleanOldUpdateFiles()
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(AppPaths.ExecutableDirectory, "*.delete_me"))
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch
        {
        }
    }

    private static void CleanupLegacyBat()
    {
        try
        {
            string batPath = Path.Combine(AppPaths.ExecutableDirectory, "_update.bat");
            if (File.Exists(batPath))
                File.Delete(batPath);
        }
        catch
        {
        }
    }

    /// <summary>Python 版のレジストリ Run エントリを削除する。</summary>
    public static bool RemovePythonRegistryAutostart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null)
                return true;

            key.DeleteValue(AppConstants.PythonRegistryName, false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPythonRegistryAutostartEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(AppConstants.PythonRegistryName) != null;
        }
        catch
        {
            return false;
        }
    }
}
