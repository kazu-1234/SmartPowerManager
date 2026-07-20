using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
    }
}
