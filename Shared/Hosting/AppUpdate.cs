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
///
/// プライベートリポにも対応する: まず認証なしで確認し、404（非公開）のときだけ、この PC の GitHub のログイン情報
/// （<see cref="GitHubCredential"/>）を付けてやり直す。公開リポならログイン情報は一切読まないので、
/// リポを後から公開にしても設定を変えずにそのまま動き、ログイン情報が外へ出ることもない。
/// </summary>
public static class AppUpdate
{
    /// <summary>リリース情報 API の待ち時間上限。</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>setup.exe ダウンロードの待ち時間上限。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>リダイレクトをたどる回数の上限（ファイルの置き場所への転送や、リポ名変更の転送に使われる）。</summary>
    private const int MaxRedirects = 5;

    /// <summary>GitHub の asset の digest の頭（"sha256:" + 16 進 64 文字）。</summary>
    private const string Sha256DigestPrefix = "sha256:";

    /// <summary>
    /// 更新確認で見つけた setup.exe の「ダウンロード URL → SHA256」。
    /// ダウンロードの入口（情報ページ・起動時ダイアログ・各アプリ独自の呼び出し）は URL とファイル名しか渡さないので、
    /// 呼び出し側を変えずに照合できるよう、ここで覚えておいて <see cref="DownloadAndInstallAsync"/> で引く。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> KnownAssetSha256 = new();

    /// <summary>
    /// HttpClient はプロセスで 1 個を使い回す（呼ぶたびに new すると接続が TIME_WAIT で残りやすい）。
    /// 処理ごとに待ち時間が違うので、Timeout は無制限にしておき、呼び出しごとに CancellationToken で切る。
    /// リダイレクトは自動でたどらず <see cref="SendAsync"/> で 1 回ずつ処理する（転送先にトークンを持ち出さないため）。
    /// </summary>
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
            var (json, authenticated) = await GetReleaseJsonAsync(apiUrl, cts.Token);
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
                var (downloadUrl, assetFileName, assetSha256) = SelectSetupAsset(release["assets"] as JArray, authenticated);
                if (downloadUrl != null && assetSha256 != null)
                    KnownAssetSha256[downloadUrl] = assetSha256;

                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpdateAvailable,
                    Message = AppHost.FormatString("Update_Available", latestVersion, CurrentVersion),
                    LatestVersion = latestVersion,
                    ReleasePageUrl = htmlUrl,
                    DownloadUrl = downloadUrl,
                    AssetFileName = assetFileName,
                    AssetSha256 = assetSha256
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
    /// リリース情報（JSON）を取る。まず認証なしで取り、GitHub API が 404（非公開リポ）を返したときだけ
    /// この PC のログイン情報を付けてやり直す。authenticated = ログイン情報を使って取れたか。
    /// ログイン情報が無い・使えないときは、認証なしの結果（404）のままエラーにする。
    /// </summary>
    private static async Task<(string Json, bool Authenticated)> GetReleaseJsonAsync(string apiUrl, CancellationToken ct)
    {
        const string accept = "application/vnd.github+json";
        using (var anonymous = await SendAsync(apiUrl, null, accept, ct))
        {
            if (anonymous.StatusCode != System.Net.HttpStatusCode.NotFound || !IsGitHubApi(new Uri(apiUrl)))
            {
                anonymous.EnsureSuccessStatusCode();
                return (await anonymous.Content.ReadAsStringAsync(ct), false);
            }
        }

        string? token = await GitHubCredential.TryGetTokenAsync();
        using var response = await SendAsync(apiUrl, token, accept, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(ct), token != null);
    }

    /// <summary>
    /// GET を送り、リダイレクトを 1 回ずつ自分でたどる。
    /// トークンは https://api.github.com 宛てのときだけ付け、ほかのホスト（ファイルの置き場所など）へは付けない。
    /// http への転送はたどらない（トークンを付けない通信でも、ダウンロードした setup.exe のすり替えを防ぐため）。
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(string url, string? token, string accept, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (int i = 0; ; i++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException($"Refused non-HTTPS URL: {uri.Host}");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);
            if (token != null && IsGitHubApi(uri))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // 本文は呼び出し側で読む（大きな setup.exe を一度にメモリへ読み込まないよう、ヘッダーまでで返す）
            var response = await SharedClient.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            int status = (int)response.StatusCode;
            if (status is < 300 or >= 400 || response.Headers.Location == null)
                return response;

            response.Dispose();
            if (i >= MaxRedirects)
                throw new HttpRequestException("Too many redirects");
            // Location は相対のこともあるので、今の URL を基準に組み立てる
            uri = new Uri(uri, response.Headers.Location);
        }
    }

    private static bool IsGitHubApi(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals(GitHubCredential.ApiHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Inno の setup.exe を優先選択する（単体アプリ exe は選ばない）。
    /// setup が無い場合のみ他の exe（後方互換）。
    /// </summary>
    /// <param name="authenticated">
    /// 非公開リポ（ログイン情報で取れた）とき true。browser_download_url はログインしたブラウザ専用なので、
    /// 代わりに API の asset URL（https://api.github.com/.../releases/assets/{id}）を使う。
    /// </param>
    private static (string? DownloadUrl, string? FileName, string? Sha256) SelectSetupAsset(JArray? assets, bool authenticated)
    {
        if (assets == null)
            return (null, null, null);

        string? fallbackUrl = null;
        string? fallbackName = null;
        string? fallbackSha256 = null;

        foreach (var asset in assets.OfType<JObject>())
        {
            string? name = asset["name"]?.ToString();
            string? url = asset[authenticated ? "url" : "browser_download_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                continue;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            string? sha256 = ParseSha256Digest(asset["digest"]?.ToString());

            // 最優先: *-setup.exe / *setup*.exe
            if (name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                return (url, name, sha256);

            fallbackUrl ??= url;
            fallbackName ??= name;
            fallbackSha256 ??= sha256;
        }

        return (fallbackUrl, fallbackName, fallbackSha256);
    }

    /// <summary>
    /// GitHub の asset の digest（"sha256:&lt;16 進 64 文字&gt;"）から 16 進の部分を小文字で取り出す。
    /// 無い・形が違う（ほかの方式のハッシュなど）ときは null にして、照合しない扱いにする。
    /// </summary>
    private static string? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(Sha256DigestPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string hex = digest[Sha256DigestPrefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
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
    /// <param name="expectedSha256">
    /// setup.exe の SHA256（16 進）。省略すると、更新確認のときに GitHub から取った値を URL で引く。
    /// どちらも無ければ照合しない（digest を返さない古いリリースでも今までどおり更新できるように）。
    /// </param>
    public static async Task<string> DownloadAndInstallAsync(
        string downloadUrl,
        string fileName,
        IProgress<string>? progress = null,
        string? expectedSha256 = null)
    {
        // asset 名はリリース側から来る値なので、フォルダ区切りを含んでいても Temp の外へ書かないよう名前だけにする
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return AppHost.GetString("Update_SetupRequired");

        expectedSha256 ??= KnownAssetSha256.TryGetValue(downloadUrl, out string? known) ? known : null;

        // Temp に DL（インストール先の exe を上書きしない）
        string downloadDir = Path.Combine(Path.GetTempPath(), AppHost.Profile.AppName + "Update");
        Directory.CreateDirectory(downloadDir);
        string targetPath = Path.Combine(downloadDir, fileName);

        progress?.Report(AppHost.GetString("Update_Downloading"));

        using (var cts = new CancellationTokenSource(DownloadTimeout))
        {
            // 非公開リポの asset URL（api.github.com）のときだけ、その場でログイン情報を読み直して付ける（保持しない）。
            // octet-stream を指定すると、GitHub はファイルの置き場所（別ホスト）へ転送する。転送先にはトークンを付けない
            Uri uri = new(downloadUrl);
            string? token = IsGitHubApi(uri) ? await GitHubCredential.TryGetTokenAsync() : null;
            using var response = await SendAsync(downloadUrl, token, "application/octet-stream", cts.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var file = File.Create(targetPath);
            await body.CopyToAsync(file, cts.Token);
        }

        // ダウンロードしたものが、リリースに載っている setup.exe と同じか確かめる（途中で壊れた・すり替わったものを起動しない）
        if (expectedSha256 != null)
        {
            string actual = await ComputeSha256Async(targetPath);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(targetPath); } catch { /* 消せなくても起動はしない */ }
                throw new InvalidOperationException(GetStringOrDefault("Update_HashMismatch", HashMismatchFallbackText));
            }
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

    /// <summary>
    /// 照合が合わなかったときの文言（アプリの .resw に Update_HashMismatch が無いときに使う。
    /// 共通部を同期しただけで .resw を直していないアプリでも、意味の通る文が出るように）。
    /// </summary>
    private const string HashMismatchFallbackText =
        "ダウンロードしたファイルがリリースの内容と一致しません。Release ページから手動で更新してください。";

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>リソースの文字列を取る。キーが無い（空が返る・例外になる）ときは fallback。</summary>
    private static string GetStringOrDefault(string key, string fallback)
    {
        try
        {
            string value = AppHost.GetString(key);
            return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
        }
        catch
        {
            return fallback;
        }
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
