using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinUiShared;

/// <summary>
/// Program.Main と App の起動で全アプリ共通の手順。
/// アプリ固有の引数（BlueShift の --reset-gamma、自動起動の --sync-autostart など）は
/// <see cref="RunMain"/> の handlePreLaunchArgs で各アプリが処理する。
/// </summary>
public static class AppBootstrap
{
    /// <summary>--exit 後、既存プロセスの終了（ファイルロック解除）を待つ時間。インストーラがこの後 exe を上書きする。</summary>
    private const int ExitSignalWaitMs = 1500;

    public const string ExitArg = "--exit";
    public const string BackgroundArg = "--background";
    public const string SyncAutostartArg = "--sync-autostart";
    public const string CleanupAutostartArg = "--cleanup-autostart";

    /// <summary>
    /// Program.Main の中身。--exit は WinUI を起動せずに既存プロセスへ終了依頼して戻る
    /// （終了経路は既存プロセス側の ExitApplication → Application.Exit の 1 本だけ）。
    /// </summary>
    /// <param name="args">Main の引数。</param>
    /// <param name="profile">このアプリの AppProfile（ここで AppHost に登録する）。</param>
    /// <param name="handlePreLaunchArgs">
    /// WinUI 起動前に処理したい引数があれば処理して true を返す（true ならそのまま終了）。
    /// </param>
    /// <param name="createApp">WinUI の Application（各アプリの App）を new する。</param>
    public static void RunMain(
        string[] args,
        AppProfile profile,
        Func<string[], bool>? handlePreLaunchArgs,
        Func<Application> createApp)
    {
        AppHost.Initialize(profile);

        if (HasArg(args, ExitArg))
        {
            SingleInstance.SignalExit();
            Thread.Sleep(ExitSignalWaitMs);
            return;
        }

        if (handlePreLaunchArgs?.Invoke(args) == true)
            return;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            // async/await の続きが UI スレッドに戻ってくるよう、UI スレッドの同期コンテキストを設定する
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            createApp();
        });
    }

    /// <summary>引数に arg が含まれるか（大文字小文字は区別しない）。</summary>
    public static bool HasArg(IEnumerable<string> args, string arg)
    {
        foreach (string item in args)
        {
            if (string.Equals(item, arg, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>このプロセスのコマンドライン引数に arg が含まれるか。</summary>
    public static bool HasCommandLineArg(string arg) => HasArg(Environment.GetCommandLineArgs(), arg);

    /// <summary>
    /// App の UnhandledException の共通処理。lifetime.log に残し、プロセスを落とさない。
    /// （WinUI は未処理例外でプロセスごと落ちるため。常駐アプリが黙って消えるのを防ぐ）
    /// </summary>
    public static void HandleUnhandledException(Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Debug.WriteLine(e.Exception);
        AppHost.AppendLifetimeLog($"unhandled {e.Exception?.GetType().Name}: {e.Exception?.Message}");
        e.Handled = true;
    }
}
