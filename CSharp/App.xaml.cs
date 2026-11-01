// v2.3.3

using Microsoft.UI.Xaml;
using SmartPowerManager.Services;
using System;
using WinUiShared;

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

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) =>
            AppBootstrap.HandleUnhandledException(e);

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            ConfigMigrationService.MigrateIfNeeded();
            ConfigMigrationService.RunStartupCleanup();

            // --cleanup-autostart / --sync-autostart は Program.Main（WinUI 起動前）で処理済み

            var settings = Settings.Load();
            bool launchInBackground = AppBootstrap.HasCommandLineArg(AppBootstrap.BackgroundArg);
            bool requestInteractiveShow = !launchInBackground;

            if (!SingleInstance.TryBecomePrimaryInstance(requestInteractiveShow))
            {
                Exit();
                return;
            }

            // スタートアップ（レジストリ）方式を選んでいるときは移行で Run を消さない
            if (settings.UseLogonTask)
                StartupManager.MigrateFromPythonRegistryIfNeeded();

            // primary のみタスクを更新する（--background 二重起動の CreateOrUpdate で本体が落ちるのを防ぐ）
#if !DEBUG
            // 起動時にログオンタスクを「今動いている exe」へ合わせ直す。
            // Debug ビルドで行うと、インストール版の登録が bin\Debug の exe に書き換わってしまうため Release のみ。
            // （設定画面で自動起動を切り替えたときや --sync-autostart の明示同期は Debug でも行う）
            StartupManager.SyncAutostartWithSettings(settings.AutoStart, settings.UseLogonTask);
#endif
            settings.Save();

            _runtime = new AppRuntime(this, settings);
            _runtime.Start(launchInBackground, requestInteractiveShow);
        }
    }
}
