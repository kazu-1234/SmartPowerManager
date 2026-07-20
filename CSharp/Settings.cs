using System;
using System.IO;
using Newtonsoft.Json;

namespace SmartPowerManager
{
    public class Settings
    {
        /// <summary>デフォルトはシステム連動。</summary>
        public AppThemePreference ThemePreference { get; set; } = AppThemePreference.System;

        /// <summary>ログオンタスクによる自動起動。</summary>
        public bool AutoStart { get; set; }

        /// <summary>true のときタスクトレイアイコンを表示しない。</summary>
        public bool HideTrayIcon { get; set; }

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

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonConvert.DeserializeObject<Settings>(json);
                    if (settings != null)
                        return settings;
                }
            }
            catch
            {
            }

            return new Settings();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(AppPaths.AppDataDirectory))
                    Directory.CreateDirectory(AppPaths.AppDataDirectory);

                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
            }
        }
    }
}
