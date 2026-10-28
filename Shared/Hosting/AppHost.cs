namespace WinUiShared;

/// <summary>
/// 共通コードから「今動いているアプリ」の情報（AppProfile と AppRuntime）を引くための入口。
/// 起動直後（Program.Main / App.OnLaunched の最初）に <see cref="Initialize"/> を 1 回だけ呼ぶ。
/// </summary>
public static class AppHost
{
    private static AppProfile? _profile;

    /// <summary>現在のアプリのプロファイル。Initialize 前に触ると例外（初期化漏れをすぐ気付けるように）。</summary>
    public static AppProfile Profile =>
        _profile ?? throw new InvalidOperationException("AppHost.Initialize が呼ばれていません。");

    /// <summary>常駐オブジェクト。App.OnLaunched で生成されるまでは null（--exit などの早期処理中も null）。</summary>
    public static AppRuntimeBase? Runtime { get; internal set; }

    /// <summary>
    /// プロファイルを登録する。同じプロセスで 2 回呼んでも最初の値を使う
    /// （Program.Main と App の両方から呼ばれても安全にするため）。
    /// </summary>
    public static void Initialize(AppProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile ??= profile;
    }

    /// <summary>アプリの .resw から文字列を取得する。</summary>
    public static string GetString(string key) => Profile.GetString(key);

    /// <summary>アプリの .resw から書式付き文字列を取得する。</summary>
    public static string FormatString(string key, params object[] args) => Profile.FormatString(key, args);

    /// <summary>lifetime.log に 1 行書く（終了理由など重要イベントのみ）。Initialize 前は何もしない。</summary>
    public static void AppendLifetimeLog(string message)
    {
        if (_profile != null)
            LifetimeLog.Append(_profile.AppName, message);
    }
}
