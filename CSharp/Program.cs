using System;
using WinUiShared;

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

            // --exit（インストーラからの終了依頼）と WinUI の起動は共通処理（WinUiShared.AppBootstrap）
            AppBootstrap.RunMain(args, AppSetup.Profile, HandlePreLaunchArgs, () => new App());
        }

        /// <summary>WinUI を起動せずに済む引数を処理する。処理したら true（そのまま終了）。</summary>
        private static bool HandlePreLaunchArgs(string[] args)
        {
            // アンインストール時の掃除: 自動起動の登録だけ削除して終了
            if (AppBootstrap.HasArg(args, AppBootstrap.CleanupAutostartArg))
            {
                StartupManager.CleanupAutostartOnly();
                return true;
            }

            // インストール直後など: 設定に合わせてログオンタスク（またはスタートアップ）を現行 exe へ同期して終了
            if (AppBootstrap.HasArg(args, AppBootstrap.SyncAutostartArg))
            {
                var settings = Settings.Load();
                StartupManager.SyncAutostartWithSettings(settings.AutoStart, settings.UseLogonTask);
                return true;
            }

            return false;
        }
    }
}
