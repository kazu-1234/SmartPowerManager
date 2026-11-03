using System;
using System.IO;
using Newtonsoft.Json;
using WinUiShared;

namespace SmartPowerManager
{
    public class Settings : AppHostSettingsBase
    {
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

        /// <summary>読み込み。無い・壊れているときは既定値（壊れていれば 1 つ前の保存版 .bak で救済）。</summary>
        public static Settings Load() =>
            LoadFrom<Settings>();

    }
}
