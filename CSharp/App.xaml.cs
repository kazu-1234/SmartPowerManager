// v2.0.16

using Microsoft.UI.Xaml;
using SmartPowerManager.Services;
using System;
using System.Diagnostics;

namespace SmartPowerManager
{
    public partial class App : Application
    {
        private AppRuntime? _runtime;

        internal static AppRuntime Runtime =>
            (Current as App)?._runtime
            ?? throw new InvalidOperationException("App runtime is not initialized.");

        public App()
        {
            InitializeComponent();
            // × で MainWindow を破棄してもトレイ常駐を続ける（明示 Exit まで終了しない）
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Debug.WriteLine(e.Exception);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            UpdateChecker.LatestReleaseApiUrl = AppConstants.GitHubApiUrl;

            ConfigMigrationService.MigrateIfNeeded();
            ConfigMigrationService.RunStartupCleanup();

            if (HasCommandLineArg("--cleanup-autostart"))
            {
                StartupManager.CleanupAutostartOnly();
                Exit();
                return;
            }

            if (HasCommandLineArg("--sync-autostart"))
            {
                var syncSettings = Settings.Load();
                StartupManager.SyncAutostartWithSettings(syncSettings.AutoStart);
                Exit();
                return;
            }

            StartupManager.MigrateFromPythonRegistryIfNeeded();

            var settings = Settings.Load();
            StartupManager.SyncAutostartWithSettings(settings.AutoStart);
            settings.Save();

            bool launchInBackground = HasCommandLineArg("--background");
            bool requestInteractiveShow = !launchInBackground;

            if (!SingleInstanceManager.TryBecomePrimaryInstance(requestInteractiveShow))
            {
                Exit();
                return;
            }

            _runtime = new AppRuntime(this, settings);
            _runtime.Start(launchInBackground, requestInteractiveShow);
        }

        private static bool HasCommandLineArg(string arg)
        {
            foreach (string item in Environment.GetCommandLineArgs())
            {
                if (string.Equals(item, arg, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
