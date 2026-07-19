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

    public static bool ApplyAutoStart(bool enable)
    {
        if (!enable)
            return RemoveLogonTask();

        ConfigMigrationService.RemovePythonRegistryAutostart();
        return CreateLogonTask();
    }

    public static void ValidateAutoStart(bool autoStartEnabled)
    {
        if (!autoStartEnabled)
            return;

        // コマンド不一致時だけでなく、優先度・Delay など設定更新のため再登録する
        CreateLogonTask();
    }

    public static string? GetRegisteredCommand() => GetLogonTaskCommand();

    public static void MigrateFromPythonRegistryIfNeeded()
    {
        if (!ConfigMigrationService.IsPythonRegistryAutostartEnabled())
            return;

        if (!IsAutoStartEnabled())
            ApplyAutoStart(true);
        else
            ConfigMigrationService.RemovePythonRegistryAutostart();
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
            // ログオン直後の遅延を抑え、優先度をやや上げる
            definition.Settings.Priority = ProcessPriorityClass.AboveNormal;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

            definition.Triggers.Add(new LogonTrigger
            {
                UserId = Environment.UserDomainName + @"\" + Environment.UserName,
                Delay = TimeSpan.Zero
            });
            definition.Actions.Add(new ExecAction(exePath, BackgroundArg, workingDir));

            // 既存タスクがあっても上書き登録し、高速化設定を反映する
            folder.RegisterTaskDefinition(
                LogonTaskName,
                definition,
                TaskCreation.CreateOrUpdate,
                null,
                null,
                TaskLogonType.InteractiveToken);
            ConfigMigrationService.RemovePythonRegistryAutostart();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create logon task: {ex.Message}");
            return false;
        }
    }

    private static bool RemoveLogonTask()
    {
        try
        {
            using TaskService taskService = new();
            TaskFolder? folder = taskService.GetFolder(TaskFolder);
            folder?.DeleteTask(LogonTaskName, false);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove logon task: {ex.Message}");
            return false;
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
        return Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("実行ファイルのパスを取得できません。");
    }
}
