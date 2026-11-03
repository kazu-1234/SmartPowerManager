namespace WinUiShared;

/// <summary>
/// 自動起動の窓口（共通部が使う最小限の操作）。<see cref="AppProfile.Autostart"/> に渡す。
/// 各アプリにあった StartupManager（AutostartService の薄いラッパー）の共通化。
/// </summary>
public interface IAutostartController
{
    /// <summary>スタートアップ（レジストリ Run）方式も選べるか（false ならログオンタスク専用）。</summary>
    bool CanChooseRegistryRun { get; }

    /// <summary>タスクか Run のどちらかで登録されているか。</summary>
    bool IsEnabled();

    /// <summary>登録・解除して設定に合わせる。登録に失敗したら false。</summary>
    bool Sync(bool enable, bool useLogonTask);

    /// <summary>登録されているコマンド（設定画面の表示用）。preferLogonTask は今の方式を先に探すため。</summary>
    string? GetRegisteredCommand(bool preferLogonTask);

    /// <summary>旧方式（古い名前の Run・スタートアップのショートカット・古いタスク）の残骸を片付ける（起動時に 1 回）。</summary>
    void MigrateLegacy(bool autoStart, bool useLogonTask);

    /// <summary>登録をすべて消す（アンインストール時・自動起動 OFF での終了時）。</summary>
    void Cleanup();
}

/// <summary>
/// <see cref="AutostartIdentity"/> を渡すだけで使える汎用の自動起動の窓口（ほとんどのアプリはこれで足りる）。
/// アプリ固有の後始末（旧アプリ名の残骸など）は引数のコールバックで渡す。
/// </summary>
public sealed class AutostartController : IAutostartController
{
    private readonly AutostartIdentity _identity;
    private readonly Action? _extraCleanup;
    private readonly Action<bool, bool>? _customMigrate;
    private readonly string[] _rootTasksToRemove;

    /// <param name="identity">タスク名・Run の名前など（公開後は変えない）。</param>
    /// <param name="extraCleanup">登録・解除のたびに追加で消すもの（SmartPowerManager の旧版の残骸など）。</param>
    /// <param name="customMigrate">
    /// 起動時の移行を独自に行うとき（引数は autoStart, useLogonTask）。渡すと既定の移行処理は行わない。
    /// </param>
    /// <param name="rootTasksToRemove">タスクスケジューラの直下に作っていた旧版のタスク名（移行時に消す。BlueShift など）。</param>
    public AutostartController(
        AutostartIdentity identity,
        Action? extraCleanup = null,
        Action<bool, bool>? customMigrate = null,
        params string[] rootTasksToRemove)
    {
        _identity = identity;
        _extraCleanup = extraCleanup;
        _customMigrate = customMigrate;
        _rootTasksToRemove = rootTasksToRemove;
    }

    public AutostartIdentity Identity => _identity;

    public bool CanChooseRegistryRun => _identity.AllowRegistryRun;

    public bool IsEnabled() => AutostartService.IsEnabled(_identity);

    public bool Sync(bool enable, bool useLogonTask) =>
        AutostartService.Sync(_identity, enable, useLogonTask, _extraCleanup);

    public string? GetRegisteredCommand(bool preferLogonTask) =>
        AutostartService.GetRegisteredCommand(_identity, preferLogonTask);

    /// <summary>
    /// 既定の移行:
    /// ・スタートアップ（Run）方式を選んでいるとき … 今の Run は残し、古い名前の Run とショートカットだけ消す。
    /// ・ログオンタスク方式のとき … 古い Run が残っていてタスクが無く、自動起動 ON なら、タスクへ移す。
    ///   それ以外は古い Run とショートカットを消す。
    /// （以前の各アプリ版は「タスクか Run があるか」で判定していたため、Run 方式も選べるアプリでは
    ///   Run が残っていると移行の分岐に入らなかった。起動直後の同期が結局タスクを作るので実害は小さかった）
    /// </summary>
    public void MigrateLegacy(bool autoStart, bool useLogonTask)
    {
        if (_rootTasksToRemove.Length > 0)
            AutostartService.RemoveRootTasks(_rootTasksToRemove);

        if (_customMigrate != null)
        {
            _customMigrate(autoStart, useLogonTask);
            return;
        }

        if (_identity.AllowRegistryRun && !useLogonTask)
        {
            AutostartService.CleanLegacyArtifactsKeepCurrentRun(_identity);
            return;
        }

        if (autoStart
            && AutostartService.IsRegistryPresent(_identity)
            && !AutostartService.IsLogonTaskEnabled(_identity))
        {
            // Sync（タスク方式）は Run とショートカットの削除も行う
            Sync(true, useLogonTask: true);
            return;
        }

        AutostartService.RemoveRegistryAndShortcuts(_identity);
    }

    public void Cleanup() => Sync(false, useLogonTask: true);
}
