using Microsoft.Windows.ApplicationModel.Resources;

namespace SmartPowerManager
{
    /// <summary>
    /// 文字列リソース（ja-JP / en-US）へのアクセス。
    /// </summary>
    public static class Strings
    {
        private static readonly ResourceLoader Loader = new();

        public static string Get(string key)
        {
            return Loader.GetString(key);
        }

        public static string Format(string key, params object[] args)
        {
            string format = Loader.GetString(key);
            return string.IsNullOrEmpty(format) ? key : string.Format(format, args);
        }
    }
}
