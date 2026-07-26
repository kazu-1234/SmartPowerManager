using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SmartPowerManager.Services;
using System;
using System.Threading;

namespace SmartPowerManager
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 単一ファイル展開先を Windows App SDK に伝える（unpackaged self-contained 必須）
            Environment.SetEnvironmentVariable(
                "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
                AppContext.BaseDirectory);

            if (HasArg(args, "--exit"))
            {
                SingleInstanceManager.SignalExit();
                Thread.Sleep(1500);
                return;
            }

            if (HasArg(args, "--cleanup-autostart"))
            {
                StartupManager.CleanupAutostartOnly();
                return;
            }

            if (HasArg(args, "--sync-autostart"))
            {
                var settings = Settings.Load();
                StartupManager.SyncAutostartWithSettings(settings.AutoStart);
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }

        private static bool HasArg(string[] args, string arg)
        {
            foreach (string item in args)
            {
                if (string.Equals(item, arg, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
