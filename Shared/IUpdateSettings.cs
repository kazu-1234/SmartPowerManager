namespace WinUiShared;

/// <summary>更新確認に必要な設定。各アプリの Settings が実装する。</summary>
public interface IUpdateSettings
{
    /// <summary>起動時に更新を自動確認するか（情報ページのトグルが書き換える）。</summary>
    bool AutoCheckUpdateOnStartup { get; set; }
    DateTime? LastUpdateCheckUtc { get; set; }
    void Save();
}
