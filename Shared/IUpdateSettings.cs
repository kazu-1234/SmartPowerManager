namespace WinUiShared;

/// <summary>更新確認に必要な設定。各アプリの Settings が実装する。</summary>
public interface IUpdateSettings
{
    bool AutoCheckUpdateOnStartup { get; }
    DateTime? LastUpdateCheckUtc { get; set; }
    void Save();
}
