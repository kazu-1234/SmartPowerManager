using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SmartPowerManager
{
    /// <summary>
    /// GitHub Releases 経由のアップデート確認。
    /// App.xaml.cs の LatestReleaseApiUrl に API URL を設定して利用する。
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>
        /// GitHub Releases API URL（未設定時は確認不可）。
        /// 例: https://api.github.com/repos/owner/repo/releases/latest
        /// </summary>
        public static string? LatestReleaseApiUrl { get; set; }

        public static string CurrentVersion
        {
            get
            {
                Version? version = Assembly.GetExecutingAssembly().GetName().Version;
                if (version == null)
                    return "1.0.0";

                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        public static async Task<UpdateCheckResult> CheckForUpdateAsync()
        {
            if (string.IsNullOrWhiteSpace(LatestReleaseApiUrl))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.NotConfigured,
                    Message = Strings.Get("Update_NotConfigured")
                };
            }

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartPowerManager");
                client.Timeout = TimeSpan.FromSeconds(15);

                string json = await client.GetStringAsync(LatestReleaseApiUrl);
                var release = JObject.Parse(json);
                string? tagName = release["tag_name"]?.ToString();
                string? htmlUrl = release["html_url"]?.ToString();

                if (string.IsNullOrWhiteSpace(tagName))
                {
                    return new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.Error,
                        Message = Strings.Get("Update_FetchFailed")
                    };
                }

                string latestVersion = tagName.TrimStart('v', 'V');
                int compare = CompareVersions(latestVersion, CurrentVersion);

                if (compare > 0)
                {
                    string? downloadUrl = null;
                    string? assetFileName = null;
                    if (release["assets"] is JArray assets)
                    {
                        foreach (var asset in assets.OfType<JObject>())
                        {
                            string? name = asset["name"]?.ToString();
                            if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset["browser_download_url"]?.ToString();
                                assetFileName = name;
                                break;
                            }
                        }
                    }

                    return new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.UpdateAvailable,
                        Message = Strings.Format("Update_Available", latestVersion, CurrentVersion),
                        LatestVersion = latestVersion,
                        ReleasePageUrl = htmlUrl,
                        DownloadUrl = downloadUrl,
                        AssetFileName = assetFileName
                    };
                }

                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    Message = Strings.Format("Update_UpToDate", CurrentVersion),
                    LatestVersion = latestVersion,
                    ReleasePageUrl = htmlUrl
                };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    Message = Strings.Format("Update_Error", ex.Message)
                };
            }
        }

        private static int CompareVersions(string a, string b)
        {
            string[] partsA = a.Split('.');
            string[] partsB = b.Split('.');
            int length = Math.Max(partsA.Length, partsB.Length);

            for (int i = 0; i < length; i++)
            {
                int numA = i < partsA.Length && int.TryParse(partsA[i], out int va) ? va : 0;
                int numB = i < partsB.Length && int.TryParse(partsB[i], out int vb) ? vb : 0;
                if (numA != numB)
                    return numA.CompareTo(numB);
            }

            return 0;
        }
    }

    public enum UpdateCheckStatus
    {
        NotConfigured,
        UpToDate,
        UpdateAvailable,
        Error
    }

    public class UpdateCheckResult
    {
        public UpdateCheckStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? LatestVersion { get; set; }
        public string? ReleasePageUrl { get; set; }
        public string? DownloadUrl { get; set; }
        public string? AssetFileName { get; set; }
    }
}
