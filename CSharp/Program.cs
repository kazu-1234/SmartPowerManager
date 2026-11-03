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

            // --exit（インストーラからの終了依頼）、--sync-autostart / --cleanup-autostart、WinUI の起動は共通処理
            // （WinUiShared.AppBootstrap）。このアプリ固有の引数は無い
            AppBootstrap.RunMain(args, AppSetup.Profile, handlePreLaunchArgs: null, () => new App());
        }
    }
}
