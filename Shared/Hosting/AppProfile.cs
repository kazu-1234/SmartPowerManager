namespace WinUiShared;

/// <summary>
/// MainWindow を閉じた（×）ときにプロセスをどうするか。
/// </summary>
public enum WindowCloseBehavior
{
    /// <summary>常駐型（BlueShift / SPM）。窓だけ破棄し、プロセスはトレイ等で生き続ける。</summary>
    KeepRunning,

    /// <summary>ウィンドウ型（LeftHandDevice / MusicPlayer）。窓が閉じたらプロセスも終了する。</summary>
    ExitProcess
}

/// <summary>
/// アプリごとに違う「名前・機能の ON/OFF」をまとめた器。
/// 各アプリは App.xaml.cs の先頭付近で 1 個だけ作り、<see cref="AppHost.Initialize"/> に渡す。
/// 共通コード（二重起動・トレイ・更新確認・復帰監視など）はここに書かれた値だけを見て動く。
/// </summary>
public sealed class AppProfile
{
    /// <summary>
    /// アプリの識別名。%AppData%\&lt;AppName&gt;、lifetime.log、HTTP の User-Agent、
    /// 更新 DL 先の Temp フォルダ名、Mutex 名の元に使う。既存ユーザーのデータ位置が変わるので、公開後は変えない。
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// 二重起動用の Mutex / イベント名の版（"v1" など）。名前は <c>Global\{AppName}_SingleInstance_{版}</c> になる。
    /// 旧版と新版で名前が違うとインストーラの <c>--exit</c> が届かなくなるため、既存アプリは現行値を維持する。
    /// </summary>
    public string InstanceNameVersion { get; init; } = "v1";

    /// <summary>GitHub Releases API の URL（未設定なら更新確認は NotConfigured）。</summary>
    public string? LatestReleaseApiUrl { get; init; }

    /// <summary>× の挙動（常駐するか、プロセスごと閉じるか）。</summary>
    public WindowCloseBehavior CloseBehavior { get; init; } = WindowCloseBehavior.ExitProcess;

    /// <summary>タスクトレイを使うか（常駐型のみ）。</summary>
    public bool UseTray { get; init; }

    /// <summary>
    /// スリープ復帰・画面復帰の監視（SystemEventWindow + ResumeReapplyCoordinator）を使うか。
    /// 復帰時に何かを再適用するアプリ（ガンマ・スケジュール・再生など）だけ true にする。
    /// </summary>
    public bool UseResumeMonitor { get; init; }

    /// <summary>復帰監視用の非表示ウィンドウのクラス名（他アプリと衝突しない名前にする）。</summary>
    public string SystemEventWindowClassName { get; init; } = string.Empty;

    /// <summary>トレイ通知用の非表示ウィンドウのクラス名。</summary>
    public string TrayWindowClassName { get; init; } = string.Empty;

    /// <summary>トレイ非表示ウィンドウのタイトル（Spy++ などで見える名前。動作には関係しない）。</summary>
    public string TrayWindowTitle { get; init; } = string.Empty;

    /// <summary>トレイアイコンに使う Assets 内の .ico ファイル名。無ければ exe のアイコンを使う。</summary>
    public string TrayIconFileName { get; init; } = "AppIcon.ico";

    /// <summary>
    /// ログオン自動起動（タスクスケジューラ）の同期処理。AutoStart を使うアプリだけ渡す。
    /// 引数 true で登録、false で解除。実体は各アプリの StartupManager（→ WinUiShared.AutostartService）。
    /// </summary>
    public Func<bool, bool>? SyncAutostart { get; init; }

    /// <summary>リソース文字列の取得（各アプリの Strings.Get を渡す）。共通 UI の文言もアプリの .resw から引く。</summary>
    public required Func<string, string> GetString { get; init; }

    /// <summary>書式付きリソース文字列の取得（各アプリの Strings.Format を渡す）。</summary>
    public required Func<string, object[], string> FormatString { get; init; }

    /// <summary>自動起動を使うか（<see cref="SyncAutostart"/> が渡されているか）。</summary>
    public bool UseAutoStart => SyncAutostart != null;

    /// <summary>%AppData%\&lt;AppName&gt;。設定・PID・信号ファイルの置き場所。</summary>
    public string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
}
