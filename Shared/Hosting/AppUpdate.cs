using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Microsoft.UI.Xaml;
using Newtonsoft.Json.Linq;

namespace WinUiShared;

/// <summary>
/// GitHub Releases 経由の更新確認と、setup.exe のダウンロード・起動（全アプリ共通）。
/// URL・User-Agent・Temp フォルダ名は AppProfile から取る。文言はアプリの .resw（Update_* キー）。
/// UI 側の流れ（InfoBar・起動時ダイアログ）は <see cref="UpdateFlow"/> / <see cref="InfoUpdateUi"/>。
/// </summary>
public static class AppUpdate
{
    /// <summary>リリース情報 API の待ち時間上限。</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>setup.exe ダウンロードの待ち時間上限。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// HttpClient はプロセスで 1 個を使い回す（呼ぶたびに new すると接続が TIME_WAIT で残りやすい）。
    /// 処理ごとに待ち時間が違うので、Timeout は無制限にしておき、呼び出しごとに CancellationToken で切る。
    /// </summary>
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub API は User-Agent が無いと 403 を返す
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppHost.Profile.AppName);
        return client;
    });

    /// <summary>
    /// 表示用の現在版（Major.Minor.Build = vX.Y.Z）。csproj の Version から取る。
    /// 共有 DLL 自身ではなく exe 側（EntryAssembly）の版を読む点に注意。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            Version? version = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
            if (version == null)
                return "1.0.0";

            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    // ───────── 更新確認 ─────────

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        string? apiUrl = AppHost.Profile.LatestReleaseApiUrl;
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.NotConfigured,
                Message = AppHost.GetString("Update_NotConfigured")
            };
        }

        try
        {
            using var cts = new CancellationTokenSource(CheckTimeout);
            string json = await SharedClient.Value.GetStringAsync(apiUrl, cts.Token);
            var release = JObject.Parse(json);
            string? tagName = release["tag_name"]?.ToString();
            string? htmlUrl = release["html_url"]?.ToString();

            if (string.IsNullOrWhiteSpace(tagName))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    Message = AppHost.GetString("Update_FetchFailed")
                };
            }

            string latestVersion = tagName.TrimStart('v', 'V');
            int compare = CompareVersions(latestVersion, CurrentVersion);

            if (compare > 0)
            {
                var (downloadUrl, assetFileName) = SelectSetupAsset(release["assets"] as JArray);

                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpdateAvailable,
                    Message = AppHost.FormatString("Update_Available", latestVersion, CurrentVersion),
                    LatestVersion = latestVersion,
                    ReleasePageUrl = htmlUrl,
                    DownloadUrl = downloadUrl,
                    AssetFileName = assetFileName
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpToDate,
                Message = AppHost.FormatString("Update_UpToDate", CurrentVersion),
                LatestVersion = latestVersion,
                ReleasePageUrl = htmlUrl
            };
        }
        catch (Exception ex)
        {
            // 自前の待ち時間切れは TaskCanceledException になる。旧実装（HttpClient.Timeout）と同じくエラー表示にする
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Error,
                Message = AppHost.FormatString("Update_Error", ex.Message)
            };
        }
    }

    /// <summary>
    /// Inno の setup.exe を優先選択する（単体アプリ exe は選ばない）。
    /// setup が無い場合のみ他の exe（後方互換）。
    /// </summary>
    private static (string? DownloadUrl, string? FileName) SelectSetupAsset(JArray? assets)
    {
        if (assets == null)
            return (null, null);

        string? fallbackUrl = null;
        string? fallbackName = null;

        foreach (var asset in assets.OfType<JObject>())
        {
            string? name = asset["name"]?.ToString();
            string? url = asset["browser_download_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                continue;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            // 最優先: *-setup.exe / *setup*.exe
            if (name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                return (url, name);

            fallbackUrl ??= url;
            fallbackName ??= name;
        }

        return (fallbackUrl, fallbackName);
    }

    /// <summary>"1.2.10" と "1.2.9" のような版を数値として比べる（文字列比較だと 10 &lt; 9 になるため）。</summary>
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

    // ───────── 更新のインストール ─────────

    /// <summary>
    /// Inno Setup の setup.exe を Temp にダウンロードして起動し、アプリを終了する。
    /// folder インストール／単体 exe 差し替えには対応しない。
    /// </summary>
    public static async Task<string> DownloadAndInstallAsync(
        string downloadUrl,
        string fileName,
        IProgress<string>? progress = null)
    {
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return AppHost.GetString("Update_SetupRequired");

        // Temp に DL（インストール先の exe を上書きしない）
        string downloadDir = Path.Combine(Path.GetTempPath(), AppHost.Profile.AppName + "Update");
        Directory.CreateDirectory(downloadDir);
        string targetPath = Path.Combine(downloadDir, fileName);

        progress?.Report(AppHost.GetString("Update_Downloading"));

        using (var cts = new CancellationTokenSource(DownloadTimeout))
        {
            await using var response = await SharedClient.Value.GetStreamAsync(downloadUrl, cts.Token);
            await using var file = File.Create(targetPath);
            await response.CopyToAsync(file, cts.Token);
        }

        progress?.Report(AppHost.GetString("Update_LaunchingSetup"));

        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true
        });

        // インストーラが exe を上書きできるようアプリを終了する（終了経路は ExitApplication に一本化）
        if (AppHost.Runtime != null)
            AppHost.Runtime.ExitApplication("update-install");
        else
            Application.Current.Exit();

        return AppHost.GetString("Update_SetupStarted");
    }

    // ───────── UI フロー（UpdateFlow へ文言と処理を渡す薄い入口） ─────────

    /// <summary>Info ページの「最終確認日時」表示文字列。</summary>
    public static string FormatLastUpdateCheckDisplay(DateTime? lastCheckUtc) =>
        UpdateFlow.FormatLastUpdateCheckDisplay(lastCheckUtc, AppHost.GetString, AppHost.Profile.FormatString);

    /// <summary>手動確認（結果を返し、最終確認日時を保存する）。</summary>
    public static Task<UpdateCheckResult> CheckAndRecordAsync(IUpdateSettings settings) =>
        UpdateFlow.CheckAndRecordAsync(settings, CheckForUpdateAsync);

    /// <summary>プロセス初回の MainWindow 表示後に 1 回だけ行う起動時確認。</summary>
    public static Task TryStartupCheckAsync(Window window, IUpdateSettings settings) =>
        UpdateFlow.TryStartupCheckAsync(
            window,
            settings,
            AppHost.GetString,
            AppHost.Profile.FormatString,
            CheckForUpdateAsync,
            (url, name) => DownloadAndInstallAsync(url, name));
}
