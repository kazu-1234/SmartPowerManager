using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WinUiShared;

/// <summary>
/// AppRuntimeBase が MainWindow に求める最小の操作。各アプリの MainWindow が実装する。
/// </summary>
public interface IAppMainWindow
{
    /// <summary>最初のページへ遷移 → 位置復元 → Activate の順で表示する（ちらつき防止のため順序固定）。</summary>
    void PrepareAndActivate(string? pageTag);

    /// <summary>既に開いている窓で指定ページへ移動する（トレイの「設定を開く」など）。</summary>
    void NavigateToPageTag(string pageTag);

    /// <summary>× 押下直後に位置・サイズを保存する。</summary>
    void SaveWindowBoundsFromRuntime();
}

/// <summary>
/// プロセス寿命を管理する常駐オブジェクトの共通実装（全アプリ共通の正本）。
///
/// 構造: Program.Main → App（1 個）→ AppRuntime（このクラスの派生、1 個）→ MainWindow（0 か 1 個、表示のたびに new）
///
/// ここで共通に行うこと:
/// - 二重起動イベント（表示依頼 / 終了依頼）の監視
/// - MainWindow の生成・前面化・破棄、起動時の更新確認
/// - タスクトレイ（AppProfile.UseTray）と、ログオン直後に失敗したときの再試行
/// - スリープ復帰・画面復帰の監視（AppProfile.UseResumeMonitor）
/// - 終了処理の一本化（<see cref="ExitApplication"/> だけがプロセスを終わらせる）
///
/// 派生クラス（各アプリの AppRuntime）は CreateMainWindow と、必要なら On～Core の override だけ書く。
/// </summary>
public abstract class AppRuntimeBase : IDisposable
{
    /// <summary>トレイ初期化に失敗したときの再試行タイミング（ログオン直後はシェル未準備で失敗することがある）。</summary>
    private static readonly int[] TrayRetryDelaysMs = { 2000, 5000, 15000 };

    /// <summary>「常駐準備完了」バルーンを出す試行タイミング（トレイ表示直後は出ないことがあるため複数回）。</summary>
    private static readonly int[] StartupReadyBalloonDelaysMs = { 1500, 2500, 5000 };

    /// <summary>二重起動リスナーが名前付きイベントを待つ間隔。この間隔でファイル信号も確認する。</summary>
    private const int ListenerPollMs = 500;

    private readonly Application _app;
    private readonly IAppHostSettings _hostSettings;

    private Window? _mainWindow;
    private TrayMessageWindow? _trayMessageWindow;
    private SystemEventWindow? _systemEventWindow;
    private ResumeReapplyCoordinator? _resumeCoordinator;
    private CancellationTokenSource? _listenerCts;
    private bool _trayInitialized;
    private bool _systemEventInitialized;
    private bool _startupUpdateCheckScheduled;
    private bool _startupReadyNotified;

    // 終了フラグは BG スレッド（リスナー・タイマー）からも読むので volatile にする
    private volatile bool _isExitingProcess;

    /// <summary>ExitApplication の二重実行防止（0 = 未実行 / 1 = 実行済み）。別スレッドから同時に呼ばれても 1 回だけ通す。</summary>
    private int _exitStarted;

#if DEBUG
    private Timer? _debuggerDetachTimer;
#endif

    /// <param name="app">Application（終了時に Exit を呼ぶ）。</param>
    /// <param name="settings">アプリの設定。テーマ・トレイ非表示・自動起動・更新確認・窓位置に使う。</param>
    protected AppRuntimeBase(Application app, IAppHostSettings settings)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _hostSettings = settings ?? throw new ArgumentNullException(nameof(settings));

        // 二重起動リスナーやタイマーは BG スレッドから来るため、UI スレッドの Dispatcher を起動時に保持する
        UiDispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("AppRuntime must be created on the UI thread.");

        AppHost.Runtime = this;
    }

    /// <summary>UI スレッドの DispatcherQueue。BG スレッドから UI を触るときは必ずこれに積む。</summary>
    protected DispatcherQueue UiDispatcher { get; }

    protected static AppProfile Profile => AppHost.Profile;

    /// <summary>終了処理が始まったか。true の間は新しい窓を作らない・処理を始めない。</summary>
    public bool IsExitingProcess => _isExitingProcess;

    /// <summary>今開いている MainWindow（閉じていれば null）。</summary>
    protected Window? CurrentMainWindow => _mainWindow;

    /// <summary>復帰再適用コーディネータ（UseResumeMonitor が false なら null）。</summary>
    protected ResumeReapplyCoordinator? ResumeCoordinator => _resumeCoordinator;

    /// <summary>トレイアイコン（トレイ未使用・初期化前は null）。</summary>
    protected TrayIconService? TrayIcon => _trayInitialized ? _trayMessageWindow?.TrayIcon : null;

    /// <summary>
    /// スリープ復帰・画面復帰・セッション解除など。
    /// 既定の OnResumeApplyCore / OnResumeApplyDirectCore がこのイベントを発火する。
    /// </summary>
    public event Action? SystemResumed;

    // ───────── 派生クラスが実装・上書きする部分 ─────────

    /// <summary>MainWindow を new して返す（IAppMainWindow を実装した Window）。</summary>
    protected abstract Window CreateMainWindow();

    /// <summary>
    /// コア機能（ガンマ・スケジューラ・デバイス等）の初期化。トレイより前に呼ばれる。
    /// --background 起動でも必ず呼ばれる（ログオン起動で未適用のまま残らないように、スキップ分岐を作らない）。
    /// 起動直後の再適用が必要なアプリは、ここで <c>ResumeCoordinator?.BeginStartupPeriod()</c> を呼ぶ。
    /// </summary>
    protected virtual void OnStartCore()
    {
    }

    /// <summary>新しい MainWindow を表示した直後（窓ごとの購読などに使う）。</summary>
    protected virtual void OnMainWindowCreated(Window window)
    {
    }

    /// <summary>二重起動側から「画面を開いて」と頼まれた（BG スレッドから呼ばれる）。</summary>
    protected virtual void OnInteractiveShowRequested() => ShowOrCreateMainWindow();

    /// <summary>
    /// 終了時のコア機能の後片付け（タイマー停止・デバイス切断・設定保存など）。
    /// MainWindow を閉じた後、トレイ破棄・Mutex 解放の前に呼ばれる。
    /// </summary>
    protected virtual void OnExitCore()
    {
    }

    /// <summary>復帰時の再適用（UI スレッド）。既定は SystemResumed を発火。</summary>
    protected virtual void OnResumeApplyCore() => RaiseSystemResumed();

    /// <summary>
    /// UI スレッドへ積めなかったときの直接適用（任意スレッドから呼ばれる。UI を触らない処理だけにする）。
    /// 既定は SystemResumed を発火。
    /// </summary>
    protected virtual void OnResumeApplyDirectCore() => RaiseSystemResumed();

    /// <summary>
    /// true のとき、ウォッチドッグ（通常 30 秒・復帰直後 2.5 秒間隔）で <see cref="OnResumeWatchdogCore"/> を呼ぶ。
    /// false のときウォッチドッグは OnResumeApplyCore を呼ぶ（ResumeReapplyCoordinator の既定動作）。
    /// </summary>
    protected virtual bool UseResumeWatchdog => false;

    /// <summary>ウォッチドッグの定期確認（UI スレッド）。</summary>
    protected virtual void OnResumeWatchdogCore()
    {
    }

    /// <summary>
    /// true のとき、復帰後に <see cref="NeedsContinueForcing"/> が true の間は最大 180 秒 Force 適用を続ける（BlueShift のガンマ）。
    /// </summary>
    protected virtual bool UseContinueForcing => false;

    /// <summary>期待した状態にまだなっていなければ true（任意スレッドから呼ばれる）。</summary>
    protected virtual bool NeedsContinueForcing() => false;

    /// <summary>
    /// トレイメニューに足す項目（既定の「設定を開く / 終了」の上に並ぶ）。右クリックのたびに UI スレッドで呼ばれる。
    /// null を返すと既定の 2 項目だけ。
    /// </summary>
    protected virtual IReadOnlyList<TrayMenuItem>? GetTrayMenuItems() => null;

    protected void RaiseSystemResumed() => SystemResumed?.Invoke();

    // ───────── 起動 ─────────

    /// <param name="launchInBackground">--background（ログオン起動）。窓を出さずに常駐だけ始める。</param>
    /// <param name="requestInteractiveShow">ユーザー操作の起動なら true（窓を出す）。</param>
    public void Start(bool launchInBackground, bool requestInteractiveShow)
    {
        ThemeService.Initialize(_hostSettings.ThemePreference);
        StartListeners();

        if (Profile.UseResumeMonitor)
        {
            EnsureResumeCoordinator();
            EnsureSystemEventMonitor();
        }

        // コアをトレイより先に初期化する（ログオン直後の未適用を防ぐ。SPM / BlueShift で確立した順序）
        OnStartCore();

        if (ShouldUseTray())
        {
            EnsureTray();
            if (!_trayInitialized)
                ScheduleTrayRetries();
        }
#if DEBUG
        else if (IsKeepRunning && Debugger.IsAttached)
        {
            // デバッガで常駐型を動かすときはトレイを出さないので、デバッガ切断でプロセスを終わらせる
            StartDebuggerDetachWatch();
        }
#endif

        // ウィンドウ型は常駐の手段が無いので、--background でも必ず窓を出す（見えないプロセスが残るのを防ぐ）
        if (!IsKeepRunning || requestInteractiveShow || !launchInBackground)
            ShowOrCreateMainWindow();

        if (Profile.UseTray)
            ScheduleStartupReadyNotification();
    }

    private static bool IsKeepRunning => Profile.CloseBehavior == WindowCloseBehavior.KeepRunning;

    private static bool ShouldUseTray()
    {
        if (!Profile.UseTray)
            return false;
#if DEBUG
        // デバッグ実行中はトレイ常駐せず、ウィンドウを閉じたら通常終了する（デバッガ再起動の手間を省く）
        if (Debugger.IsAttached)
            return false;
#endif
        return true;
    }

    // ───────── MainWindow ─────────

    /// <summary>既存の MainWindow を前面表示、なければ生成して表示する（どのスレッドから呼んでもよい）。</summary>
    public void ShowOrCreateMainWindow(string? pageTag = null)
    {
        if (_isExitingProcess)
            return;

        // 常に UI スレッドへ積む。BG スレッド（二重起動リスナー）からも同じ経路で安全に呼べる
        UiDispatcher.TryEnqueue(() => ShowOrCreateMainWindowCore(pageTag));
    }

    private void ShowOrCreateMainWindowCore(string? pageTag)
    {
        if (_isExitingProcess)
            return;

        if (_mainWindow != null)
        {
            BringWindowToForeground(_mainWindow);
            if (pageTag != null)
                AsMainWindow(_mainWindow).NavigateToPageTag(pageTag);
            return;
        }

        // Window は Close で内部状態が壊れやすく Hide→Show の再利用は不具合の元なので、表示のたびに new する
        Window window = CreateMainWindow();
        _mainWindow = window;
        window.Closed += MainWindow_Closed;
        AsMainWindow(window).PrepareAndActivate(pageTag);
        OnMainWindowCreated(window);
        ScheduleStartupUpdateCheckIfNeeded();
    }

    private static IAppMainWindow AsMainWindow(Window window) =>
        window as IAppMainWindow
        ?? throw new InvalidOperationException("MainWindow は IAppMainWindow を実装してください。");

    private void ScheduleStartupUpdateCheckIfNeeded()
    {
        if (_startupUpdateCheckScheduled || _mainWindow == null)
            return;

        _startupUpdateCheckScheduled = true;
        _ = AppUpdate.TryStartupCheckAsync(_mainWindow, _hostSettings);
    }

    /// <summary>MainWindow の × 直後（Closing）に呼ばれる。位置保存のみ行う。</summary>
    public void OnMainWindowClosing(Window window)
    {
        if (_isExitingProcess || !ReferenceEquals(window, _mainWindow))
            return;

        try
        {
            AsMainWindow(window).SaveWindowBoundsFromRuntime();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SaveWindowBounds failed: {ex.Message}");
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        try { ((Window)sender).Closed -= MainWindow_Closed; } catch { }

        if (!ReferenceEquals(_mainWindow, sender))
            return;

        _mainWindow = null;

        if (_isExitingProcess)
            return;

        // ウィンドウ型: 窓が閉じたらプロセスも終了する（窓は閉じ済みなので再 Close はしない）
        if (!IsKeepRunning)
        {
            ExitApplication("window-close");
            return;
        }

#if DEBUG
        // 常駐型でもデバッガ実行中はトレイが無いので、閉じたら終了する
        if (!ShouldUseTray())
            ExitApplication("debug-window-close");
#endif
    }

    /// <summary>最小化を戻し、前面に出す。</summary>
    private static void BringWindowToForeground(Window window)
    {
        try
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            window.AppWindow.IsShownInSwitchers = true;
            window.AppWindow.Show();
            window.Activate();

            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BringWindowToForeground failed: {ex.Message}");
        }
    }

    // ───────── 終了 ─────────

    /// <summary>
    /// アプリを完全終了する唯一の入口（トレイの「終了」/ --exit / 更新インストール / ウィンドウ型の ×）。
    /// どのスレッドから何回呼ばれても、実際の終了処理は 1 回だけ走る。
    /// </summary>
    /// <param name="reason">lifetime.log に残す終了理由。</param>
    public void ExitApplication(string reason = "unknown")
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
            return;

        _isExitingProcess = true;
        AppHost.AppendLifetimeLog(reason);

        try
        {
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
        }
        catch
        {
        }
        _listenerCts = null;

#if DEBUG
        _debuggerDetachTimer?.Dispose();
        _debuggerDetachTimer = null;
#endif

        _resumeCoordinator?.Dispose();
        _resumeCoordinator = null;

        _systemEventWindow?.Dispose();
        _systemEventWindow = null;
        _systemEventInitialized = false;

        // 窓を先に閉じる（ページの Unloaded がサービスを触っても、まだサービスが生きている順序にする）
        Window? window = _mainWindow;
        _mainWindow = null;
        if (window != null)
        {
            try { window.Closed -= MainWindow_Closed; } catch { }
            try { window.Close(); } catch { }
        }

        try
        {
            OnExitCore();
        }
        catch (Exception ex)
        {
            AppHost.AppendLifetimeLog($"exit-core-failed {ex.GetType().Name}: {ex.Message}");
        }

        _trayMessageWindow?.Dispose();
        _trayMessageWindow = null;
        _trayInitialized = false;

        // 自動起動 OFF のまま終了するときは、念のためログオンタスクの残骸を消す
        if (Profile.SyncAutostart != null && !_hostSettings.AutoStart)
        {
            try { Profile.SyncAutostart(false); } catch { }
        }

        try { SingleInstance.Release(); } catch { }

        try { _app.Exit(); } catch { }
    }

    public void Dispose()
    {
        ExitApplication("dispose");
        GC.SuppressFinalize(this);
    }

    /// <summary>lifetime.log に 1 行書く（派生クラス名 AppRuntime.AppendLifetimeLog でも呼べる）。</summary>
    public static void AppendLifetimeLog(string message) => AppHost.AppendLifetimeLog(message);

    // ───────── タスクトレイ ─────────

    /// <summary>設定の「トレイアイコンを隠す」を反映する（隠しても常駐は続ける）。</summary>
    public void ApplyTrayIconVisibility()
    {
        if (_trayMessageWindow == null)
            return;

        if (_hostSettings.HideTrayIcon)
            _trayMessageWindow.TrayIcon.Hide();
        else
            _trayMessageWindow.TrayIcon.Show();
    }

    private void EnsureTray()
    {
        if (_trayInitialized)
            return;

        try
        {
            // MainWindow の WndProc は差し替えない（白画面の原因）。トレイ専用の非表示ウィンドウを使う
            _trayMessageWindow = new TrayMessageWindow();
            var tray = _trayMessageWindow.TrayIcon;
            tray.OpenMainWindowRequested += () => ShowOrCreateMainWindow();
            tray.OpenSettingsRequested += () => ShowOrCreateMainWindow("Settings");
            tray.ExitRequested += () => UiDispatcher.TryEnqueue(() => ExitApplication("tray-menu"));
            tray.CustomMenuItemsProvider = GetTrayMenuItems;
            ApplyTrayIconVisibility();
            _trayInitialized = true;
        }
        catch (Exception ex)
        {
            // トレイ初期化失敗時は通常ウィンドウのみで動作を続ける（再試行は ScheduleTrayRetries）
            Debug.WriteLine($"Tray init failed: {ex.Message}");
            _trayMessageWindow?.Dispose();
            _trayMessageWindow = null;
            _trayInitialized = false;
        }
    }

    /// <summary>ログオン直後などシェル未準備でトレイ初期化が失敗した場合の遅延再試行。</summary>
    private void ScheduleTrayRetries()
    {
        foreach (int delayMs in TrayRetryDelaysMs)
        {
            int captured = delayMs;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(captured).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (_isExitingProcess || _trayInitialized)
                    return;

                UiDispatcher.TryEnqueue(() =>
                {
                    if (_isExitingProcess || _trayInitialized)
                        return;
                    EnsureTray();
                });
            });
        }
    }

    /// <summary>トレイ HWND が死んでいたら作り直し、生きていればアイコンを付け直す（explorer 再起動・復帰対策）。</summary>
    private void EnsureTrayAlive(bool reAddIcon)
    {
        if (_isExitingProcess || !ShouldUseTray())
            return;

        if (_trayInitialized && _trayMessageWindow is { IsAlive: true })
        {
            if (reAddIcon && !_hostSettings.HideTrayIcon)
            {
                try { _trayMessageWindow.TrayIcon.ReAdd(); }
                catch (Exception ex) { Debug.WriteLine($"Tray ReAdd failed: {ex.Message}"); }
            }
            return;
        }

        _trayMessageWindow?.Dispose();
        _trayMessageWindow = null;
        _trayInitialized = false;
        EnsureTray();
    }

    /// <summary>コア初期化後にトレイバルーンで常駐準備完了を知らせる（VS 外での動作確認用）。</summary>
    private void ScheduleStartupReadyNotification()
    {
        if (_startupReadyNotified)
            return;
        _startupReadyNotified = true;

        _ = Task.Run(async () =>
        {
            var start = DateTime.UtcNow;
            foreach (int delayMs in StartupReadyBalloonDelaysMs)
            {
                try
                {
                    var wait = start.AddMilliseconds(delayMs) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (_isExitingProcess)
                    return;

                var shown = new TaskCompletionSource<bool>();
                if (!UiDispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            // 設定で起動時通知を OFF にしていれば出さない（表示直前に読むので起動直後の切替も効く）
                            if (_isExitingProcess || _hostSettings.HideTrayIcon
                                || !_hostSettings.ShowStartupNotification
                                || !_trayInitialized || _trayMessageWindow == null)
                            {
                                shown.TrySetResult(false);
                                return;
                            }

                            _trayMessageWindow.TrayIcon.ShowBalloon(
                                AppHost.GetString("Tray_StartupReadyTitle"),
                                AppHost.GetString("Tray_StartupReadyBody"));
                            shown.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Startup ready balloon failed: {ex.Message}");
                            shown.TrySetResult(false);
                        }
                    }))
                {
                    shown.TrySetResult(false);
                }

                if (await shown.Task.ConfigureAwait(false))
                    return;
            }
        });
    }

    // ───────── スリープ復帰・画面復帰 ─────────

    private void EnsureResumeCoordinator()
    {
        if (_resumeCoordinator != null)
            return;

        _resumeCoordinator = new ResumeReapplyCoordinator(
            UiDispatcher,
            () => _isExitingProcess,
            OnResumeApply,
            OnResumeApplyDirect,
            UseResumeWatchdog ? OnResumeWatchdog : null,
            UseContinueForcing ? NeedsContinueForcingSafe : null);
        _resumeCoordinator.Start();
    }

    /// <summary>復帰監視窓。失敗時・HWND が死んでいたらフラグを戻し再試行可能にする。</summary>
    private void EnsureSystemEventMonitor()
    {
        if (!Profile.UseResumeMonitor)
            return;

        if (_systemEventInitialized)
        {
            if (_systemEventWindow is { IsAlive: true })
                return;

            _systemEventWindow?.Dispose();
            _systemEventWindow = null;
            _systemEventInitialized = false;
        }

        try
        {
            EnsureResumeCoordinator();
            _systemEventWindow = new SystemEventWindow(Profile.SystemEventWindowClassName);
            _systemEventWindow.SystemDisplayStateChanged += () => _resumeCoordinator?.NotifyResume();
            _systemEventWindow.SystemSuspending += () => _resumeCoordinator?.NotifySuspend();
            _systemEventInitialized = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"System event monitor init failed: {ex.Message}");
            _systemEventWindow?.Dispose();
            _systemEventWindow = null;
            _systemEventInitialized = false;
        }
    }

    /// <summary>常駐に必要な監視窓・トレイが生きているか確かめ、死んでいれば作り直す。</summary>
    private void EnsureResidentLifetime()
    {
        EnsureSystemEventMonitor();
        EnsureTrayAlive(reAddIcon: true);
    }

    private void OnResumeApply()
    {
        if (_isExitingProcess)
            return;

        try
        {
            EnsureResidentLifetime();
            OnResumeApplyCore();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnResumeApply failed: {ex.Message}");
        }
    }

    private void OnResumeApplyDirect()
    {
        if (_isExitingProcess)
            return;

        try
        {
            OnResumeApplyDirectCore();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnResumeApplyDirect failed: {ex.Message}");
        }
    }

    private void OnResumeWatchdog()
    {
        if (_isExitingProcess)
            return;

        try
        {
            EnsureResidentLifetime();
            OnResumeWatchdogCore();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnResumeWatchdog failed: {ex.Message}");
        }
    }

    private bool NeedsContinueForcingSafe()
    {
        if (_isExitingProcess)
            return false;

        try
        {
            return NeedsContinueForcing();
        }
        catch
        {
            return false;
        }
    }

    // ───────── 二重起動リスナー ─────────

    private void StartListeners()
    {
        var showEvent = SingleInstance.InteractiveShowEvent;
        var exitEvent = SingleInstance.ExitEvent;
        if (showEvent == null && exitEvent == null)
            return;

        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;

        if (showEvent != null)
            Task.Run(() => ListenShowLoop(showEvent, token), token);

        if (exitEvent != null)
            Task.Run(() => ListenExitLoop(exitEvent, token), token);
    }

    /// <summary>Event に加え、ファイル show_signal も 500ms ポールで拾う（Event が開けなかった二次起動の対策）。</summary>
    private void ListenShowLoop(EventWaitHandle handle, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool signaled;
            try
            {
                signaled = handle.WaitOne(ListenerPollMs);
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!signaled)
                signaled = SingleInstance.TryConsumeShowSignal();

            if (token.IsCancellationRequested)
                break;

            if (signaled)
            {
                try { OnInteractiveShowRequested(); }
                catch (Exception ex) { Debug.WriteLine($"Show request failed: {ex.Message}"); }
            }
        }
    }

    /// <summary>終了依頼は「イベントで起こされた + 終了ファイルがある」ときだけ実行する（誤爆で終了しないため）。</summary>
    private void ListenExitLoop(EventWaitHandle handle, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                handle.WaitOne(ListenerPollMs);
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (token.IsCancellationRequested)
                break;

            if (SingleInstance.TryConsumeExitSignal())
                UiDispatcher.TryEnqueue(() => ExitApplication("exit-signal"));
        }
    }

#if DEBUG
    private void StartDebuggerDetachWatch()
    {
        _debuggerDetachTimer = new Timer(_ =>
        {
            if (Debugger.IsAttached || _isExitingProcess)
                return;

            UiDispatcher.TryEnqueue(() =>
            {
                if (!_isExitingProcess)
                    ExitApplication("debug-detach");
            });
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
#endif

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
