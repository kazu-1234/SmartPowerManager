using System.Diagnostics;
using System.Text;

namespace WinUiShared;

/// <summary>
/// この PC に保存されている GitHub のログイン情報（トークン）を、プライベートリポの更新確認のためだけに取り出す。
/// 取り出し元は git の資格情報（Git Credential Manager）→ GitHub CLI（gh auth token）の順。
/// トークンはファイル・設定・ログに一切残さず、呼び出し側もその場で使って捨てる（<see cref="AppUpdate"/>）。
/// ログイン画面やパスワードの入力欄は出さない（出せる状況なら何も取らずに諦める）。
/// </summary>
internal static class GitHubCredential
{
    /// <summary>資格情報の読み取りの待ち時間上限（入力待ちで止まったときもこれで打ち切る）。</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>トークンを付けてよい唯一の送り先。ほかのホストには絶対に付けない。</summary>
    public const string ApiHost = "api.github.com";

    /// <summary>
    /// トークンを返す。見つからない・読めないときは null（呼び出し側は「更新を確認できない」として扱う）。
    /// </summary>
    public static async Task<string?> TryGetTokenAsync()
    {
        // git credential fill … 標準入力で「どこの資格情報か」を渡すと、保存済みの username / password を返す。
        // GitHub の場合 password はトークン（Git Credential Manager がブラウザログインで取得したもの）
        string? output = await RunAsync("git", "credential fill", "protocol=https\nhost=github.com\n\n");
        string? token = output == null ? null : ReadField(output, "password");
        if (!string.IsNullOrWhiteSpace(token))
            return token.Trim();

        // git に無ければ GitHub CLI のログイン情報を使う（gh が入っていなければ null）
        output = await RunAsync("gh", "auth token", null);
        token = output?.Trim();
        return string.IsNullOrWhiteSpace(token) || token.Contains(' ') ? null : token;
    }

    /// <summary>「key=value」の行の並びから値を取り出す（git credential の出力形式）。</summary>
    private static string? ReadField(string output, string key)
    {
        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
                return line[(key.Length + 1)..].TrimEnd('\r');
        }
        return null;
    }

    /// <summary>コマンドを窓なしで実行し、成功したときだけ標準出力を返す（失敗・見つからない・時間切れは null）。</summary>
    private static async Task<string?> RunAsync(string fileName, string arguments, string? stdin)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            // 標準エラーは読むだけで捨てる（エラー文に資格情報が混ざっても表示・記録しないため）
            RedirectStandardError = true,
            // 先頭に BOM が付くと git が 1 行目（protocol=https）を読めずに失敗するので、BOM なしの UTF-8 にする
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // 資格情報が無いときにログイン画面や入力欄を出さない（常駐アプリが勝手に窓を出さないように）。
        // GCM_INTERACTIVE … Git Credential Manager の対話をしない / GIT_TERMINAL_PROMPT … git が端末で聞かない
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // gh が「新しい版があります」などの案内を出力に混ぜないように
        psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch
        {
            // git / gh が入っていない
            return null;
        }

        using (process)
        {
            try
            {
                if (stdin != null)
                    await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(ReadTimeout);
                await process.WaitForExitAsync(cts.Token);
                await stderr;
                return process.ExitCode == 0 ? await stdout : null;
            }
            catch
            {
                // 時間切れ（何かの入力待ちで止まった）など。残さないようにツリーごと終わらせる
                try { process.Kill(entireProcessTree: true); } catch { }
                Debug.WriteLine($"[Update] credential read failed: {fileName}");
                return null;
            }
        }
    }
}
