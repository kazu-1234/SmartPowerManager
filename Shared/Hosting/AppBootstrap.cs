using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinUiShared;

/// <summary>
/// Program.Main と App の起動で全アプリ共通の手順。
/// アプリ固有の引数（BlueShift の --reset-gamma、自動起動の --sync-autostart など）は
/// <see cref="RunMain"/> の handlePreLaunchArgs で各アプリが処理する。
/// </summary>
/// <summary><see cref="AppBootstrap.BeginPrimaryLaunch"/> の結果（AppRuntimeBase.Start にそのまま渡す）。</summary>
public sealed record PrimaryLaunch(bool LaunchInBackground, bool RequestInteractiveShow);

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

        // 自動起動の掃除・同期（インストーラーが呼ぶ）。どちらも WinUI を起動せずに終わる。
        // 自動起動を使わないアプリにこの引数が来ても、窓を出さずに終わらせる
        // （インストーラーが waituntilterminated で待っているので、普通に起動すると閉じるまで止まってしまう）
        if (HasArg(args, CleanupAutostartArg))
        {
            if (profile.Autostart != null)
                profile.Autostart.Cleanup();
            else if (handlePreLaunchArgs?.Invoke(args) == true)
                return;
            return;
        }

        if (HasArg(args, SyncAutostartArg))
        {
            if (profile.Autostart != null && profile.LoadSettings != null)
            {
                IAppHostSettings settings = profile.LoadSettings();
                profile.Autostart.Sync(settings.AutoStart, settings.UseLogonTask);
            }
            else if (handlePreLaunchArgs?.Invoke(args) == true)
            {
                return;
            }
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
    /// 原因を追えるよう、スタックトレースまで残す。
    /// </summary>
    public static void HandleUnhandledException(Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Debug.WriteLine(e.Exception);
        AppHost.AppendLifetimeLog($"unhandled {DescribeException(e.Exception)}");
        e.Handled = true;
    }

    private static int _globalLoggingInstalled;

    /// <summary>
    /// UI スレッド以外の未処理例外（AppDomain）と、待たれずに失敗した Task の例外を lifetime.log に残す。
    /// AppDomain の未処理例外はプロセスが終わるので、onCrash で最低限の後始末（画面の点灯を戻すなど）ができる。
    /// （SubMonitorSleeper / MusicPlayer が個別に持っていた処理の共通化。何度呼んでも 1 回だけ登録する）
    /// </summary>
    public static void InstallGlobalExceptionLogging(Action? onCrash = null)
    {
        if (Interlocked.Exchange(ref _globalLoggingInstalled, 1) != 0)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppHost.AppendLifetimeLog($"appdomain-unhandled terminating={e.IsTerminating} {DescribeException(e.ExceptionObject as Exception)}");
            if (onCrash == null)
                return;

            try { onCrash(); }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppHost.AppendLifetimeLog($"unobserved-task {DescribeException(e.Exception)}");
            // 記録したら「観測済み」にして、既定の扱い（設定によってはプロセス終了）を避ける
            e.SetObserved();
        };
    }

    /// <summary>例外の型・メッセージ・スタックトレース（内側の例外も含む）を 1 つの文字列にする。</summary>
    public static string DescribeException(Exception? ex) =>
        ex == null ? "(null)" : $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}";

    /// <summary>
    /// App.OnLaunched の共通手順（設定を読み終えたあとに呼ぶ）:
    /// --background の判定 → 単一インスタンス → 自動起動の移行 → Release のときだけ自動起動を今の exe へ同期。
    /// 二重起動だったときは既存のプロセスへ表示を頼み、app.Exit() して null を返す（呼び出し側はそのまま return する）。
    /// 戻り値で AppRuntimeBase.Start に渡す値を返す。
    /// </summary>
    /// <param name="onBecamePrimary">primary に決まった直後（イベント作成前）の処理（MusicPlayer の古い依頼の掃除など）。</param>
    public static PrimaryLaunch? BeginPrimaryLaunch(Application app, IAppHostSettings settings, Action? onBecamePrimary = null)
    {
        bool launchInBackground = HasCommandLineArg(BackgroundArg);
        bool requestInteractiveShow = !launchInBackground;

        if (!SingleInstance.TryBecomePrimaryInstance(requestInteractiveShow, onBecamePrimary))
        {
            // 既に起動中のインスタンスへ表示依頼を送ったので、このプロセスは何もせず終了する
            app.Exit();
            return null;
        }

        IAutostartController? autostart = AppHost.Profile.Autostart;
        if (autostart != null)
        {
            autostart.MigrateLegacy(settings.AutoStart, settings.UseLogonTask);
#if !DEBUG
            // 起動時にログオンタスク（またはスタートアップ）を「今動いている exe」へ合わせ直す（primary だけが行う）。
            // Debug ビルドで行うと、インストール版の登録が bin\Debug の exe に書き換わってしまうため Release のみ。
            autostart.Sync(settings.AutoStart, settings.UseLogonTask);
#endif
        }

        return new PrimaryLaunch(launchInBackground, requestInteractiveShow);
    }
}
