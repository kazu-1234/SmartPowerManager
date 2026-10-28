using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using System.Diagnostics;

namespace WinUiShared;

/// <summary>自動起動のアプリ固有識別子（SPM 正本のタスク／Run 方式）。</summary>
public sealed class AutostartIdentity
{
    public required string AppName { get; init; }
    public required string LogonTaskName { get; init; }
    public required string RegistryName { get; init; }
    public string BackgroundArg { get; init; } = "--background";
    public bool AllowRegistryRun { get; init; } = true;
    public TimeSpan LogonDelay { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan UnlockDelay { get; init; } = TimeSpan.FromSeconds(5);
    public string[] ExtraRegistryNames { get; init; } = [];
    public string[] ExtraTaskFolderPrefixes { get; init; } = [];
    public string[] ExtraTaskNames { get; init; } = [];
    public string[] ExtraPathTokens { get; init; } = [];
    public string[] StartupShortcutNames { get; init; } = [];
    public string[] StartupShortcutGlobs { get; init; } = [];
}

/// <summary>
/// ログオンタスク（Delay 15s。SessionUnlock なし）と任意のレジストリ Run を同期する（SPM 正本）。
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static string TaskFolderName(AutostartIdentity id) =>
        $"{id.AppName}_{Environment.UserName}";

    public static string TaskFolderPath(AutostartIdentity id) =>
        Path.Combine(TaskFolderName(id), id.LogonTaskName);

    public static bool IsEnabled(AutostartIdentity id) =>
        GetLogonTaskCommand(id) != null
        || (id.AllowRegistryRun && GetRegistryStartupCommand(id) != null);

    public static bool Sync(AutostartIdentity id, bool enable, bool useLogonTask = true, System.Action? extraCleanup = null)
    {
        if (!enable)
        {
            RemoveAllAutostartTasks(id);
            RemoveRegistryStartup(id);
            CleanStartupShortcuts(id);
            extraCleanup?.Invoke();
            return true;
        }

        if (!id.AllowRegistryRun || useLogonTask)
        {
            RemoveRegistryStartup(id);
            CleanStartupShortcuts(id);
            extraCleanup?.Invoke();
            bool created = CreateLogonTask(id);
            RemoveForeignAutostartTasks(id);
            return created;
        }

        RemoveAllAutostartTasks(id);
        CleanStartupShortcuts(id);
        return CreateRegistryStartup(id);
    }

    public static string? GetRegisteredCommand(AutostartIdentity id, bool preferLogonTask = true)
    {
        if (preferLogonTask)
            return GetLogonTaskCommand(id) ?? GetRegistryStartupCommand(id);
        return GetRegistryStartupCommand(id) ?? GetLogonTaskCommand(id);
    }

    public static bool IsRegistryPresent(AutostartIdentity id)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null)
                return false;

            if (key.GetValue(id.RegistryName) != null)
                return true;

            foreach (string name in id.ExtraRegistryNames)
            {
                if (key.GetValue(name) != null)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>レジストリ Run とスタートアップ .lnk のみ除去（ログオンタスクは残す）。</summary>
    public static void RemoveRegistryAndShortcuts(AutostartIdentity id)
    {
        RemoveRegistryStartup(id);
        CleanStartupShortcuts(id);
    }

    /// <summary>スタートアップ .lnk と ExtraRegistryNames のみ除去（現行 Run は残す）。</summary>
    public static void CleanLegacyArtifactsKeepCurrentRun(AutostartIdentity id)
    {
        CleanStartupShortcuts(id);
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
                return;

            foreach (string name in id.ExtraRegistryNames)
                key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    public static void RemoveRootTasks(params string[] taskNames)
    {
        try
        {
            using TaskService taskService = new();
            foreach (string name in taskNames)
            {
                try { taskService.RootFolder.DeleteTask(name, false); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove root tasks: {ex.Message}");
        }
    }

    public static string GetExecutablePath()
    {
        string? path = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("実行ファイルのパスを取得できません。");
        return Path.GetFullPath(path);
    }

    private static bool CreateRegistryStartup(AutostartIdentity id)
    {
        try
        {
            string exePath = GetExecutablePath();
            string value = $"\"{exePath}\" {id.BackgroundArg}";
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null)
                return false;

            key.SetValue(id.RegistryName, value);
            foreach (string name in id.ExtraRegistryNames)
                key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create registry startup: {ex.Message}");
            return false;
        }
    }

    private static void RemoveRegistryStartup(AutostartIdentity id)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
                return;

            key.DeleteValue(id.RegistryName, throwOnMissingValue: false);
            foreach (string name in id.ExtraRegistryNames)
                key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove registry startup: {ex.Message}");
        }
    }

    private static string? GetRegistryStartupCommand(AutostartIdentity id)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(id.RegistryName)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool CreateLogonTask(AutostartIdentity id)
    {
        try
        {
            string exePath = GetExecutablePath();
            string? workingDir = Path.GetDirectoryName(exePath);
            string folderName = TaskFolderName(id);

            using TaskService taskService = new();
            TaskFolder folder;
            try
            {
                folder = taskService.RootFolder.CreateFolder(folderName, null, false);
            }
            catch
            {
                folder = taskService.GetFolder(folderName);
            }

            TaskDefinition definition = taskService.NewTask();
            definition.RegistrationInfo.Description =
                $"{id.AppName} をログオン時にバックグラウンド起動します。";
            definition.RegistrationInfo.Author = id.AppName;
            definition.RegistrationInfo.Source = id.AppName;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            definition.Settings.AllowHardTerminate = false;
            // スリープ復帰を「未実行」と誤認して再起動しない（本体プロセスを落とすことがある）
            definition.Settings.StartWhenAvailable = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.IdleSettings.StopOnIdleEnd = false;
            definition.Settings.Priority = ProcessPriorityClass.AboveNormal;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
            definition.Settings.RestartCount = 0;

            string userId = Environment.UserDomainName + @"\" + Environment.UserName;
            definition.Triggers.Add(new LogonTrigger
            {
                UserId = userId,
                Delay = id.LogonDelay
            });
            // SessionUnlock で exe --background を再起動しない。
            // スリープ後にタスクが「未実行」と誤認すると二重起動し、
            // CreateOrUpdate が既存プロセスを落とすことがある。復帰はプロセス内 SystemEventWindow が担う。
            definition.Actions.Add(new ExecAction(exePath, id.BackgroundArg, workingDir));

            Microsoft.Win32.TaskScheduler.Task registered = folder.RegisterTaskDefinition(
                id.LogonTaskName,
                definition,
                TaskCreation.CreateOrUpdate,
                null,
                null,
                TaskLogonType.InteractiveToken);
            if (registered != null && !registered.Enabled)
                registered.Enabled = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create logon task: {ex.Message}");
            return false;
        }
    }

    private static void RemoveAllAutostartTasks(AutostartIdentity id)
    {
        try
        {
            using TaskService taskService = new();
            foreach (TaskFolder folder in GetTaskFolders(taskService, id).ToList())
            {
                foreach (Microsoft.Win32.TaskScheduler.Task task in folder.GetTasks().ToList())
                {
                    if (!IsAutostartTask(id, task))
                        continue;

                    try { folder.DeleteTask(task.Name, false); }
                    catch { }
                }

                TryDeleteEmptyFolder(taskService, folder.Name);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove logon tasks: {ex.Message}");
        }
    }

    private static void RemoveForeignAutostartTasks(AutostartIdentity id)
    {
        try
        {
            string currentExe = Path.GetFullPath(GetExecutablePath());
            string currentFolder = TaskFolderName(id);

            using TaskService taskService = new();
            foreach (TaskFolder folder in GetTaskFolders(taskService, id).ToList())
            {
                foreach (Microsoft.Win32.TaskScheduler.Task task in folder.GetTasks().ToList())
                {
                    if (!IsAutostartTask(id, task))
                        continue;

                    ExecAction? action = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
                    bool isCurrent =
                        string.Equals(folder.Name, currentFolder, StringComparison.Ordinal) &&
                        string.Equals(task.Name, id.LogonTaskName, StringComparison.Ordinal) &&
                        action != null &&
                        string.Equals(Path.GetFullPath(action.Path), currentExe, StringComparison.OrdinalIgnoreCase);

                    if (isCurrent)
                        continue;

                    try { folder.DeleteTask(task.Name, false); }
                    catch { }
                }

                TryDeleteEmptyFolder(taskService, folder.Name);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove foreign logon tasks: {ex.Message}");
        }
    }

    private static IEnumerable<TaskFolder> GetTaskFolders(TaskService taskService, AutostartIdentity id)
    {
        foreach (TaskFolder folder in taskService.RootFolder.SubFolders)
        {
            if (folder.Name.StartsWith(id.AppName, StringComparison.OrdinalIgnoreCase))
            {
                yield return folder;
                continue;
            }

            foreach (string prefix in id.ExtraTaskFolderPrefixes)
            {
                if (folder.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return folder;
                    break;
                }
            }
        }
    }

    private static bool IsAutostartTask(AutostartIdentity id, Microsoft.Win32.TaskScheduler.Task task)
    {
        if (string.Equals(task.Name, id.LogonTaskName, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (string name in id.ExtraTaskNames)
        {
            if (string.Equals(task.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        ExecAction? action = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
        return IsAutostartAction(id, action);
    }

    private static bool IsAutostartAction(AutostartIdentity id, ExecAction? action)
    {
        if (action == null)
            return false;

        string path = action.Path ?? string.Empty;
        string args = action.Arguments ?? string.Empty;
        if (path.Contains(id.AppName, StringComparison.OrdinalIgnoreCase)
            || args.Contains(id.AppName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string token in id.ExtraPathTokens)
        {
            if (path.Contains(token, StringComparison.OrdinalIgnoreCase)
                || args.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDeleteEmptyFolder(TaskService taskService, string folderName)
    {
        try
        {
            TaskFolder folder = taskService.GetFolder(folderName);
            if (!folder.GetTasks().Any())
                taskService.RootFolder.DeleteFolder(folderName, false);
        }
        catch
        {
        }
    }

    private static string? GetLogonTaskCommand(AutostartIdentity id)
    {
        try
        {
            using TaskService taskService = new();
            Microsoft.Win32.TaskScheduler.Task? task = taskService.GetTask(TaskFolderPath(id));
            ExecAction? action = task?.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
            if (action == null)
                return null;

            return string.IsNullOrWhiteSpace(action.Arguments)
                ? action.Path
                : $"{action.Path} {action.Arguments}";
        }
        catch
        {
            return null;
        }
    }

    private static void CleanStartupShortcuts(AutostartIdentity id)
    {
        try
        {
            string startupPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\Startup");

            foreach (string name in id.StartupShortcutNames)
            {
                string path = Path.Combine(startupPath, name);
                if (File.Exists(path))
                    File.Delete(path);
            }

            foreach (string glob in id.StartupShortcutGlobs)
            {
                foreach (string path in Directory.EnumerateFiles(startupPath, glob))
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }
        catch
        {
        }
    }
}
