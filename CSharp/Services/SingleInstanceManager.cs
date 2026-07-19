using System.Threading;

namespace SmartPowerManager.Services;

internal static class SingleInstanceManager
{
    private const string MutexName = "Global\\SmartPowerManager_SingleInstance_v2";
    private const string InteractiveShowEventName = "Global\\SmartPowerManager_ShowInteractive_v2";
    private const string SignalFileName = ".show_signal";

    private static Mutex? _mutex;
    private static EventWaitHandle? _interactiveShowEvent;

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

        return true;
    }

    public static EventWaitHandle? InteractiveShowEvent => _interactiveShowEvent;

    private static void SignalInteractiveShow()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(InteractiveShowEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

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

    public static void Release()
    {
        _interactiveShowEvent?.Dispose();
        _interactiveShowEvent = null;

        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
