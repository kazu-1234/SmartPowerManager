// v2.4.1

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
            // × で MainWindow を破棄してもプロセスは継続する（トレイ常駐 / 明示 Exit まで終了しない）。
            // ウィンドウ型でも同じ設定にし、終了は AppRuntimeBase.ExitApplication の 1 経路に揃える
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            UnhandledException += (_, e) => AppBootstrap.HandleUnhandledException(e);
            // UI スレッド以外の例外と、待たれずに失敗した Task の例外も lifetime.log に残す
            AppBootstrap.InstallGlobalExceptionLogging();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 旧版のスケジュールファイルを新しい置き場所へ写す（無ければ何もしない）
            ConfigMigrationService.MigrateIfNeeded();

            var settings = Settings.Load();

            // Python 版の自動起動（レジストリ Run）が残っていれば、ログオンタスクへ移す。
            // 移したあとの「Release のときだけ行う同期」で消されないよう、設定の自動起動も ON にしておく
            // （二重起動で終わるプロセスは保存しないので、ここで書き換えても害はない）
            if (settings.UseLogonTask && ConfigMigrationService.IsPythonRegistryAutostartEnabled())
                settings.AutoStart = true;

            // 単一インスタンス・自動起動の移行（AppSetup の customMigrate）と同期は共通処理。
            // 二重起動なら null（既存の窓に表示を頼んで終了済み）
            PrimaryLaunch? launch = AppBootstrap.BeginPrimaryLaunch(this, settings);
            if (launch == null)
                return;

            // 起動時の後始末（古いショートカット・更新の残骸・ログオンタスク方式なら Python 版の Run）。
            // 以前は移行より前に呼んでいたため、Python 版の Run が先に消えて移行が一度も動かなかった。
            // 移行のあと・primary に決まってからだけ行う
            ConfigMigrationService.RunStartupCleanup(settings.UseLogonTask);
            settings.Save();

            _runtime = new AppRuntime(this, settings);
            _runtime.Start(launch.LaunchInBackground, launch.RequestInteractiveShow);
        }
    }
}
