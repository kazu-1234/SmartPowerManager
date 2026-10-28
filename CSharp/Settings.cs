using System;
using System.IO;
using Newtonsoft.Json;
using WinUiShared;

namespace SmartPowerManager
{
    public class Settings : IAppHostSettings
    {
        /// <summary>デフォルトはシステム連動。</summary>
        public AppThemePreference ThemePreference { get; set; } = AppThemePreference.System;

        /// <summary>ログオンタスクによる自動起動。</summary>
        public bool AutoStart { get; set; }

        /// <summary>
        /// true: ログオンタスクで起動（既定）。
        /// false: スタートアップ（レジストリ Run）に登録。
        /// </summary>
        public bool UseLogonTask { get; set; } = true;

        /// <summary>true のときタスクトレイアイコンを表示しない。</summary>
        public bool HideTrayIcon { get; set; }

        /// <summary>true のとき起動後に「常駐準備完了」のトレイ通知を出す（キーが無い既存設定は true＝従来どおり）。</summary>
        public bool ShowStartupNotification { get; set; } = true;

        public int WindowWidth { get; set; } = 960;
        public int WindowHeight { get; set; } = 680;

        /// <summary>未保存時は -1。次回起動で位置を復元する。</summary>
        public int WindowX { get; set; } = -1;

        /// <summary>未保存時は -1。次回起動で位置を復元する。</summary>
        public int WindowY { get; set; } = -1;

        /// <summary>前回終了時に最大化されていたか。</summary>
        public bool WindowMaximized { get; set; }

        /// <summary>シャットダウン予定の監視（実行）が有効か。</summary>
        public bool MonitoringEnabledShutdown { get; set; } = true;

        /// <summary>再起動予定の監視（実行）が有効か。</summary>
        public bool MonitoringEnabledRestart { get; set; } = true;

        /// <summary>起動時（初回 MainWindow 表示時）に更新を自動確認する。</summary>
        public bool AutoCheckUpdateOnStartup { get; set; } = true;

        /// <summary>最後に更新確認した UTC 時刻。未確認は null。</summary>
        public DateTime? LastUpdateCheckUtc { get; set; }

        /// <summary>旧設定互換。読み込み時のみ使用。</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? MonitoringEnabled
        {
            get => null;
            set
            {
                if (value == false)
                {
                    MonitoringEnabledShutdown = false;
                    MonitoringEnabledRestart = false;
                }
            }
        }

        private static string SettingsFilePath =>
            Path.Combine(AppPaths.AppDataDirectory, "settings.json");

        /// <summary>読み込み。無い・壊れているときは既定値（壊れていれば 1 つ前の保存版 .bak で救済）。</summary>
        public static Settings Load() =>
            JsonSettingsFile.Load<Settings>(SettingsFilePath) ?? new Settings();

        /// <summary>保存（一時ファイル → 差し替えで、シャットダウン実行中に止まっても壊れない）。失敗は無視する。</summary>
        public void Save() => JsonSettingsFile.Save(SettingsFilePath, this);
    }
}
