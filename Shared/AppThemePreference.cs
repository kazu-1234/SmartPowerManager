namespace WinUiShared;

/// <summary>
/// アプリ内テーマ設定（システム連動 / ライト / ダーク）。
/// 設定ファイルには数値で保存されるため、既存の値（0/1/2）の順番は変えないこと。
/// </summary>
public enum AppThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}
