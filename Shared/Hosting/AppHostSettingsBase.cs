using Newtonsoft.Json;

namespace WinUiShared;

/// <summary>
/// 全アプリ共通の設定項目（テーマ・トレイ・窓位置・更新確認・自動起動）を持つ基底クラス。
/// 各アプリの Settings はこれを継承し、固有の項目だけを足す。
/// JSON のキー名は以前の各アプリの Settings と同じ（既存ユーザーの settings.json をそのまま読める）。
/// 既定値がアプリごとに違う項目（窓の既定サイズ、AutoStart など）は、派生クラスのコンストラクタで上書きする
/// （Newtonsoft はコンストラクタで既定値を入れてから、ファイルにあるキーだけを上書きする）。
/// </summary>
public abstract class AppHostSettingsBase : IAppHostSettings
{
    /// <summary>
    /// 共通の項目の JSON 上の並び順。既定（-1）より小さくして、派生クラスの項目より先に並べる
    /// （以前の各アプリの settings.json と同じ並びにし、保存し直してもファイルの差分が出ないようにするため）。
    /// </summary>
    private const int BaseOrder = -2;

    /// <summary>設定ファイル名（%AppData%\&lt;AppName&gt;\ の下）。</summary>
    public const string DefaultFileName = "settings.json";

    /// <summary>テーマ 3 択。既定はシステム連動。</summary>
    [JsonProperty(Order = BaseOrder)]
    public AppThemePreference ThemePreference { get; set; } = AppThemePreference.System;

    /// <summary>true のときタスクトレイアイコンを表示しない（常駐は続ける）。</summary>
    [JsonProperty(Order = BaseOrder)]
    public bool HideTrayIcon { get; set; }

    /// <summary>true のとき起動後に「常駐準備完了」のトレイ通知を出す（キーが無い既存設定は true＝従来どおり）。</summary>
    [JsonProperty(Order = BaseOrder)]
    public bool ShowStartupNotification { get; set; } = true;

    /// <summary>窓の幅・高さ（物理ピクセル）。既定はテンプレートの 960×680。アプリごとにコンストラクタで変える。</summary>
    [JsonProperty(Order = BaseOrder)]
    public int WindowWidth { get; set; } = 960;

    [JsonProperty(Order = BaseOrder)]
    public int WindowHeight { get; set; } = 680;

    /// <summary>未保存時は -1。次回起動で位置を復元する。</summary>
    [JsonProperty(Order = BaseOrder)]
    public int WindowX { get; set; } = -1;

    /// <summary>未保存時は -1。次回起動で位置を復元する。</summary>
    [JsonProperty(Order = BaseOrder)]
    public int WindowY { get; set; } = -1;

    /// <summary>前回終了時に最大化されていたか。</summary>
    [JsonProperty(Order = BaseOrder)]
    public bool WindowMaximized { get; set; }

    /// <summary>起動時（初回 MainWindow 表示時）に更新を自動確認する。</summary>
    [JsonProperty(Order = BaseOrder)]
    public bool AutoCheckUpdateOnStartup { get; set; } = true;

    /// <summary>最後に更新確認した UTC 時刻。未確認は null。</summary>
    [JsonProperty(Order = BaseOrder)]
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>ログオン時の自動起動（自動起動を使わないアプリでは常に false のまま）。</summary>
    [JsonProperty(Order = BaseOrder)]
    public bool AutoStart { get; set; }

    /// <summary>true: ログオンタスクで起動（既定）／ false: スタートアップ（レジストリ Run）。</summary>
    [JsonProperty(Order = BaseOrder)]
    public bool UseLogonTask { get; set; } = true;

    /// <summary>設定ファイルの場所。既定は %AppData%\&lt;AppName&gt;\settings.json。</summary>
    [JsonIgnore]
    protected virtual string SettingsFilePath => GetDefaultPath();

    /// <summary>保存時の JSON 設定（null なら既定）。</summary>
    [JsonIgnore]
    protected virtual JsonSerializerSettings? SaveSerializerSettings => null;

    /// <summary>保存（一時ファイル → 差し替えで、途中で落ちても壊れない）。失敗は無視する。</summary>
    public virtual void Save() => JsonSettingsFile.Save(SettingsFilePath, this, SaveSerializerSettings);

    /// <summary>既定の設定ファイルのパス。</summary>
    public static string GetDefaultPath() => Path.Combine(AppHost.Profile.AppDataDirectory, DefaultFileName);

    /// <summary>
    /// 読み込み。ファイルが無い・壊れているときは既定値（壊れていれば 1 つ前の保存版で救済）。
    /// 派生クラスの static Load から <c>LoadFrom&lt;Settings&gt;()</c> の形で呼ぶ。
    /// </summary>
    protected static T LoadFrom<T>(string? path = null, JsonSerializerSettings? serializerSettings = null)
        where T : AppHostSettingsBase, new() =>
        JsonSettingsFile.Load<T>(path ?? GetDefaultPath(), serializerSettings) ?? new T();
}
