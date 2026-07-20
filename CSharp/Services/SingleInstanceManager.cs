using System.Runtime.InteropServices;
using System.Threading;

namespace SmartPowerManager.Services;

internal static class SingleInstanceManager
{
#if DEBUG
    private const string MutexName = "Global\\SmartPowerManager_SingleInstance_v2_DEBUG";
    private const string InteractiveShowEventName = "Global\\SmartPowerManager_ShowInteractive_v2_DEBUG";
    private const string ExitEventName = "Global\\SmartPowerManager_Exit_v2_DEBUG";
#else
    private const string MutexName = "Global\\SmartPowerManager_SingleInstance_v2";
    private const string InteractiveShowEventName = "Global\\SmartPowerManager_ShowInteractive_v2";
    private const string ExitEventName = "Global\\SmartPowerManager_Exit_v2";
#endif

    private static Mutex? _mutex;
    private static EventWaitHandle? _interactiveShowEvent;
    private static EventWaitHandle? _exitEvent;

    private static string PidFilePath => Path.Combine(AppPaths.AppDataDirectory, ".instance_pid");

    public static bool TryBecomePrimaryInstance(bool requestInteractiveShow)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (requestInteractiveShow)
                SignalInteractiveShow();

            return false;
        }

        _interactiveShowEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            InteractiveShowEventName);
        _exitEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ExitEventName);

        TryWritePidFile();
        return true;
    }

    public static EventWaitHandle? InteractiveShowEvent => _interactiveShowEvent;
    public static EventWaitHandle? ExitEvent => _exitEvent;

    public static void SignalInteractiveShow()
    {
        TryAllowForegroundForPrimary();

        bool signaled = false;
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(InteractiveShowEventName);
            showEvent.Set();
            signaled = true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        // Event 成功時はファイル信号を出さない（CheckShowSignal との二重表示を防ぐ）
        if (signaled)
            return;

        try
        {
            string? dir = Path.GetDirectoryName(AppPaths.SignalFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(AppPaths.SignalFilePath, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
        }
    }

    /// <summary>既存インスタンスへ終了を依頼（インストーラ用）。</summary>
    public static void SignalExit()
    {
        try
        {
            using var exitEvent = EventWaitHandle.OpenExisting(ExitEventName);
            exitEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    public static void Release()
    {
        TryDeletePidFile();

        _interactiveShowEvent?.Dispose();
        _interactiveShowEvent = null;
        _exitEvent?.Dispose();
        _exitEvent = null;

        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static void TryWritePidFile()
    {
        try
        {
            if (!Directory.Exists(AppPaths.AppDataDirectory))
                Directory.CreateDirectory(AppPaths.AppDataDirectory);
            File.WriteAllText(PidFilePath, Environment.ProcessId.ToString());
        }
        catch
        {
        }
    }

    private static void TryDeletePidFile()
    {
        try
        {
            if (File.Exists(PidFilePath))
                File.Delete(PidFilePath);
        }
        catch
        {
        }
    }

    private static void TryAllowForegroundForPrimary()
    {
        try
        {
            if (!File.Exists(PidFilePath))
                return;

            if (!int.TryParse(File.ReadAllText(PidFilePath).Trim(), out int pid))
                return;

            AllowSetForegroundWindow(pid);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
