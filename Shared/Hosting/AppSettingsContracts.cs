namespace WinUiShared;

/// <summary>ウィンドウ位置・サイズ・最大化状態の保存先（各アプリの Settings が実装する）。</summary>
public interface IWindowPlacementSettings
{
    int WindowWidth { get; set; }
    int WindowHeight { get; set; }

    /// <summary>未保存時は -1。</summary>
    int WindowX { get; set; }

    /// <summary>未保存時は -1。</summary>
    int WindowY { get; set; }

    bool WindowMaximized { get; set; }

    void Save();
}

/// <summary>
/// AppRuntimeBase が必要とする設定の最小セット。
/// 各アプリの Settings はこれを実装し、固有の項目は自由に追加してよい。
/// JSON のキー名は各アプリの既存プロパティ名のまま（既存ユーザーの settings.json を壊さない）。
/// </summary>
public interface IAppHostSettings : IUpdateSettings, IWindowPlacementSettings
{
    /// <summary>テーマ 3 択（既定はシステム連動）。</summary>
    AppThemePreference ThemePreference { get; }

    /// <summary>true のときタスクトレイアイコンを出さない（常駐は続ける）。トレイを使わないアプリは常に false。</summary>
    bool HideTrayIcon { get; }

    /// <summary>
    /// true のとき起動後に「常駐準備完了」のトレイバルーンを出す。
    /// 既定実装で true を返すので、この設定を持たないアプリも従来どおり表示される（ビルドも壊れない）。
    /// </summary>
    bool ShowStartupNotification => true;

    /// <summary>ログオン自動起動。自動起動を使わないアプリは常に false。</summary>
    bool AutoStart { get; }

    new void Save();
}
