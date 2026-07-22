using Microsoft.Win32.TaskScheduler;
using System.Diagnostics;
using SmartPowerManager.Services;

namespace SmartPowerManager;

/// <summary>
/// ログオンタスクによる自動起動を管理する（レジストリ Run は使用しない）。
/// </summary>
public static class StartupManager
{
    private const string LogonTaskName = "SmartPowerManager Logon";
    private const string BackgroundArg = "--background";

    private static string TaskFolder => $"SmartPowerManager_{Environment.UserName}";
    private static string TaskFolderPath => Path.Combine(TaskFolder, LogonTaskName);

    public static bool IsAutoStartEnabled() => GetLogonTaskCommand() != null;

    /// <summary>設定のオン／オフに合わせてタスク・レジストリ・スタートアップショートカットを同期する。</summary>
    public static bool SyncAutostartWithSettings(bool enable)
    {
        if (!enable)
        {
            RemoveAllSmartPowerManagerAutostartTasks();
            ConfigMigrationService.RemoveLegacyAutostartArtifacts();
            return true;
        }

        ConfigMigrationService.RemoveLegacyAutostartArtifacts();
        bool created = CreateLogonTask();
        RemoveForeignAutostartTasks();
        return created;
    }

    /// <inheritdoc cref="SyncAutostartWithSettings"/>
    public static bool ApplyAutoStart(bool enable) => SyncAutostartWithSettings(enable);

    public static void ValidateAutoStart(bool autoStartEnabled) =>
        SyncAutostartWithSettings(autoStartEnabled);

    public static string? GetRegisteredCommand() => GetLogonTaskCommand();

    public static void MigrateFromPythonRegistryIfNeeded()
    {
        if (!ConfigMigrationService.IsPythonRegistryAutostartEnabled())
            return;

        if (!IsAutoStartEnabled())
            SyncAutostartWithSettings(true);
        else
            ConfigMigrationService.RemovePythonRegistryAutostart();
    }

    /// <summary>アプリ削除前など、残存 exe から自動起動登録だけ除去する。</summary>
    public static void CleanupAutostartOnly()
    {
        SyncAutostartWithSettings(false);
    }

    private static bool CreateLogonTask()
    {
        try
        {
            string exePath = GetExecutablePath();
            string? workingDir = Path.GetDirectoryName(exePath);

            using TaskService taskService = new();
            TaskFolder folder;
            try
            {
                folder = taskService.RootFolder.CreateFolder(TaskFolder, null, false);
            }
            catch
            {
                folder = taskService.GetFolder(TaskFolder);
            }

            TaskDefinition definition = taskService.NewTask();
            definition.RegistrationInfo.Description =
                "SmartPowerManager をログオン時にバックグラウンド起動します。";
            definition.RegistrationInfo.Author = "SmartPowerManager";
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            definition.Settings.AllowHardTerminate = false;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.IdleSettings.StopOnIdleEnd = false;
            definition.Settings.Priority = ProcessPriorityClass.AboveNormal;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

            definition.Triggers.Add(new LogonTrigger
            {
                UserId = Environment.UserDomainName + @"\" + Environment.UserName,
                // デスクトップ／表示まわりが安定してから起動（トレイのみ・機能未適用を防ぐ）
                Delay = TimeSpan.FromSeconds(15)
            });
            definition.Actions.Add(new ExecAction(exePath, BackgroundArg, workingDir));

            folder.RegisterTaskDefinition(
                LogonTaskName,
                definition,
                TaskCreation.CreateOrUpdate,
                null,
                null,
                TaskLogonType.InteractiveToken);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create logon task: {ex.Message}");
            return false;
        }
    }

    private static void RemoveAllSmartPowerManagerAutostartTasks()
    {
        try
        {
            using TaskService taskService = new();
            foreach (TaskFolder folder in GetSmartPowerManagerTaskFolders(taskService).ToList())
            {
                foreach (Microsoft.Win32.TaskScheduler.Task task in folder.GetTasks().ToList())
                {
                    if (!IsSmartPowerManagerAutostartTask(task))
                        continue;

                    try
                    {
                        folder.DeleteTask(task.Name, false);
                    }
                    catch
                    {
                    }
                }

                TryDeleteEmptyFolder(taskService, folder.Name);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove logon tasks: {ex.Message}");
        }
    }

    /// <summary>現行 exe 以外の SmartPowerManager 自動起動タスクを削除する。</summary>
    private static void RemoveForeignAutostartTasks()
    {
        try
        {
            string currentExe = Path.GetFullPath(GetExecutablePath());
            string currentFolder = TaskFolder;

            using TaskService taskService = new();
            foreach (TaskFolder folder in GetSmartPowerManagerTaskFolders(taskService).ToList())
            {
                foreach (Microsoft.Win32.TaskScheduler.Task task in folder.GetTasks().ToList())
                {
                    if (!IsSmartPowerManagerAutostartTask(task))
                        continue;

                    ExecAction? action = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
                    bool isCurrent =
                        string.Equals(folder.Name, currentFolder, StringComparison.Ordinal) &&
                        string.Equals(task.Name, LogonTaskName, StringComparison.Ordinal) &&
                        action != null &&
                        string.Equals(Path.GetFullPath(action.Path), currentExe, StringComparison.OrdinalIgnoreCase);

                    if (isCurrent)
                        continue;

                    try
                    {
                        folder.DeleteTask(task.Name, false);
                    }
                    catch
                    {
                    }
                }

                TryDeleteEmptyFolder(taskService, folder.Name);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove foreign logon tasks: {ex.Message}");
        }
    }

    private static IEnumerable<TaskFolder> GetSmartPowerManagerTaskFolders(TaskService taskService)
    {
        foreach (TaskFolder folder in taskService.RootFolder.SubFolders)
        {
            if (folder.Name.StartsWith("SmartPowerManager", StringComparison.Ordinal))
                yield return folder;
        }
    }

    private static bool IsSmartPowerManagerAutostartTask(Microsoft.Win32.TaskScheduler.Task task)
    {
        if (string.Equals(task.Name, LogonTaskName, StringComparison.OrdinalIgnoreCase))
            return true;

        ExecAction? action = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
        return IsSmartPowerManagerAutostartAction(action);
    }

    private static bool IsSmartPowerManagerAutostartAction(ExecAction? action)
    {
        if (action == null)
            return false;

        string path = action.Path ?? string.Empty;
        string args = action.Arguments ?? string.Empty;
        if (path.Contains("SmartPowerManager", StringComparison.OrdinalIgnoreCase))
            return true;

        return args.Contains("SmartPowerManager", StringComparison.OrdinalIgnoreCase);
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

    private static string? GetLogonTaskCommand()
    {
        try
        {
            using TaskService taskService = new();
            Microsoft.Win32.TaskScheduler.Task? task = taskService.GetTask(TaskFolderPath);
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

    private static string GetExecutablePath()
    {
        string? path = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("実行ファイルのパスを取得できません。");
        return Path.GetFullPath(path);
    }
}
