using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinUiShared;

/// <summary>
/// 二重起動防止と、既存インスタンスへの表示／終了依頼（名前付き Mutex + 名前付きイベント + 信号ファイル）。
/// 名前は AppProfile から作るので、アプリ側に定数を持たない。
/// BlueShift / SPM で実績のある「異常終了したプロセスの Mutex を引き継ぐ」処理を全アプリ共通にしている。
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _interactiveShowEvent;
    private static EventWaitHandle? _exitEvent;

    /// <summary>このプロセスが Mutex を所有しているか（所有していないのに ReleaseMutex すると例外になるため記録する）。</summary>
    private static bool _ownsMutex;

    // Debug ビルドは別名にして、インストール版（Release）と同時に起動して試せるようにする
#if DEBUG
    private const string NameSuffix = "_DEBUG";
#else
    private const string NameSuffix = "";
#endif

    private static AppProfile Profile => AppHost.Profile;

    // Global\ を付けるとセッションをまたいで（別ユーザーの RDP など）同じ名前を共有する。既存アプリと同じ方式。
    private static string MutexName =>
        $"Global\\{Profile.AppName}_SingleInstance_{Profile.InstanceNameVersion}{NameSuffix}";

    private static string InteractiveShowEventName =>
        $"Global\\{Profile.AppName}_ShowInteractive_{Profile.InstanceNameVersion}{NameSuffix}";

    private static string ExitEventName =>
        $"Global\\{Profile.AppName}_Exit_{Profile.InstanceNameVersion}{NameSuffix}";

    /// <summary>Mutex 名。アプリ固有の追加ロック（Music の開く依頼など）の名前の元に使う。</summary>
    public static string BaseMutexName => MutexName;

    private static string PidFilePath => Path.Combine(Profile.AppDataDirectory, ".instance_pid");

    private static string SignalFilePath => Path.Combine(Profile.AppDataDirectory, ".show_signal");

    private static string ExitSignalFilePath => Path.Combine(Profile.AppDataDirectory, ".exit_signal");

    public static EventWaitHandle? InteractiveShowEvent => _interactiveShowEvent;
    public static EventWaitHandle? ExitEvent => _exitEvent;

    /// <param name="requestInteractiveShow">
    /// true のとき、既存インスタンスへ「ユーザー操作で GUI を開く」ことを通知する。
    /// --background の二重起動では false（通知しない）。
    /// </param>
    /// <param name="onBecamePrimary">
    /// primary になれたとき、表示イベントを作る前に呼ぶ処理（任意）。
    /// 前回の残骸ファイルの掃除など、「これから届く依頼」を消さずに済む順序が必要なものに使う（Music の開く依頼）。
    /// </param>
    /// <returns>このプロセスが最初の 1 個（primary）なら true。false なら呼び出し側はすぐ終了する。</returns>
    public static bool TryBecomePrimaryInstance(bool requestInteractiveShow, Action? onBecamePrimary = null)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (TryTakeOverAbandonedOrDeadPrimary())
            {
                createdNew = true;
            }
            else
            {
                if (requestInteractiveShow)
                    SignalInteractiveShow();

                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }
        else
        {
            _ownsMutex = true;
        }

        try { onBecamePrimary?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"onBecamePrimary failed: {ex.Message}"); }

        // AutoReset: 1 回 Set すると待っている側を 1 回だけ起こして自動で戻る
        _interactiveShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InteractiveShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);

        TryWritePidFile();
        return true;
    }

    /// <summary>
    /// 放棄 Mutex、または PID ファイルのプロセスが死んでいる場合に primary を奪取する。
    /// 前回が強制終了で Mutex だけ残ったとき、「起動しても何も出ない」状態になるのを防ぐ。
    /// </summary>
    private static bool TryTakeOverAbandonedOrDeadPrimary()
    {
        if (_mutex == null)
            return false;

        // 生きている primary があれば奪取しない
        if (IsPrimaryProcessAlive())
            return false;

        try
        {
            if (_mutex.WaitOne(0))
            {
                _ownsMutex = true;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // 前オーナーが異常終了 → こちらが所有権を得た
            _ownsMutex = true;
            return true;
        }

        // WaitOne(0) 失敗でも PID が死んでいれば短時間待って Abandoned を拾う
        try
        {
            if (_mutex.WaitOne(TimeSpan.FromMilliseconds(200)))
            {
                _ownsMutex = true;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
            return true;
        }

        return false;
    }

    private static bool IsPrimaryProcessAlive()
    {
        try
        {
            if (!File.Exists(PidFilePath))
                return false;

            if (!int.TryParse(File.ReadAllText(PidFilePath).Trim(), out int pid))
                return false;

            if (pid == Environment.ProcessId)
                return false;

            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // プロセスが存在しない
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            // 権限エラー等は「生きている」とみなし、生きている primary を奪取しない
            return true;
        }
    }

    /// <summary>既存インスタンスへ「画面を開いて」と依頼する。</summary>
    public static void SignalInteractiveShow()
    {
        // 前面化の許可を primary に渡す（これが無いと Windows が前面化を拒否してタスクバー点滅だけになる）
        TryAllowForegroundForPrimary();

        bool signaled = false;
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(InteractiveShowEventName);
            showEvent.Set();
            signaled = true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SignalInteractiveShow event failed: {ex.Message}");
        }

        // Event 成功時はファイル信号を出さない（ListenShowLoop の二重表示を防ぐ）
        if (signaled)
            return;

        TryWriteSignalFile(SignalFilePath);
    }

    /// <summary>既存インスタンスへ終了を依頼する（インストーラ / --exit 用）。ファイルが本体、イベントは起床用。</summary>
    public static void SignalExit()
    {
        TryWriteSignalFile(ExitSignalFilePath);

        try
        {
            using var exitEvent = EventWaitHandle.OpenExisting(ExitEventName);
            exitEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SignalExit event failed: {ex.Message}");
        }
    }

    /// <summary>表示依頼のファイル信号があれば消費して true。</summary>
    public static bool TryConsumeShowSignal() => TryConsumeFile(SignalFilePath);

    /// <summary>終了握手ファイルがあれば消費して true。イベントだけの起床は無視する（誤爆で終了しないため）。</summary>
    public static bool TryConsumeExitSignal() => TryConsumeFile(ExitSignalFilePath);

    private static bool TryConsumeFile(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryWriteSignalFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Profile.AppDataDirectory);
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Signal file write failed: {ex.Message}");
        }
    }

    /// <summary>終了時に Mutex・イベント・PID ファイルを片付ける。</summary>
    public static void Release()
    {
        TryDeletePidFile();

        _interactiveShowEvent?.Dispose();
        _interactiveShowEvent = null;
        _exitEvent?.Dispose();
        _exitEvent = null;

        if (_mutex != null)
        {
            if (_ownsMutex)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _ownsMutex = false;
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static void TryWritePidFile()
    {
        try
        {
            Directory.CreateDirectory(Profile.AppDataDirectory);
            File.WriteAllText(PidFilePath, Environment.ProcessId.ToString());
        }
        catch
        {
        }
    }

    private static void TryDeletePidFile()
    {
        try
        {
            if (File.Exists(PidFilePath))
                File.Delete(PidFilePath);
        }
        catch
        {
        }
    }

    private static void TryAllowForegroundForPrimary()
    {
        try
        {
            if (!File.Exists(PidFilePath))
                return;

            if (!int.TryParse(File.ReadAllText(PidFilePath).Trim(), out int pid))
                return;

            AllowSetForegroundWindow(pid);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
