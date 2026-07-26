using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI.Xaml;

namespace SmartPowerManager.Services;

/// <summary>
/// Inno Setup の setup.exe をダウンロードして起動する更新ヘルパー。
/// folder インストール／単体 exe 差し替えには対応しない。
/// </summary>
public static class UpdateInstallerService
{
    public static async Task<string> DownloadAndInstallAsync(string downloadUrl, string fileName, IProgress<string>? progress = null)
    {
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Strings.Get("Update_SetupRequired");

        // Temp に DL（インストール先の exe を上書きしない）
        string downloadDir = Path.Combine(Path.GetTempPath(), "SmartPowerManagerUpdate");
        Directory.CreateDirectory(downloadDir);
        string targetPath = Path.Combine(downloadDir, fileName);

        progress?.Report(Strings.Get("Update_Downloading"));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartPowerManager");

        await using (var response = await client.GetStreamAsync(downloadUrl))
        await using (var file = File.Create(targetPath))
        {
            await response.CopyToAsync(file);
        }

        progress?.Report(Strings.Get("Update_LaunchingSetup"));

        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true
        });

        // インストーラが上書きできるようアプリを終了
        Application.Current.Exit();
        return Strings.Get("Update_SetupStarted");
    }
}
