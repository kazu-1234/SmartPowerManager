// v2.0.0

using Microsoft.UI.Xaml;
using SmartPowerManager.Services;
using System.Diagnostics;

namespace SmartPowerManager
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
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
            StartupManager.MigrateFromPythonRegistryIfNeeded();

            bool launchInBackground = HasCommandLineArg("--background");
            bool requestInteractiveShow = !launchInBackground;

            if (!SingleInstanceManager.TryBecomePrimaryInstance(requestInteractiveShow))
            {
                Exit();
                return;
            }

            var settings = Settings.Load();
            if (StartupManager.IsAutoStartEnabled())
                settings.AutoStart = true;
            settings.Save();

            ThemeService.Initialize(settings.ThemePreference);

            _window = new MainWindow(
                settings,
                launchInBackground,
                requestInteractiveShow,
                SingleInstanceManager.InteractiveShowEvent);
            _window.Activate();
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
