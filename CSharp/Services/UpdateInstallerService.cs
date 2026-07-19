using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI.Xaml;

namespace SmartPowerManager.Services;

public static class UpdateInstallerService
{
    public static async Task<string> DownloadAndInstallAsync(string downloadUrl, string fileName, IProgress<string>? progress = null)
    {
        string currentExe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        if (!currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return "開発モードでは自動更新できません";

        string downloadDir = Path.GetDirectoryName(currentExe) ?? AppPaths.ExecutableDirectory;
        string targetPath = Path.Combine(downloadDir, fileName);

        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase))
            targetPath += ".new";

        progress?.Report("ダウンロード中...");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartPowerManager");

        await using var response = await client.GetStreamAsync(downloadUrl);
        await using var file = File.Create(targetPath);
        await response.CopyToAsync(file);

        progress?.Report("インストール中...");
        return ExecuteRenameSwap(currentExe, targetPath, downloadDir, Path.GetFileName(currentExe));
    }

    private static string ExecuteRenameSwap(string currentExe, string newExePath, string currentDir, string currentName)
    {
        string oldName = $"{currentName}.{Environment.ProcessId}.delete_me";
        string oldPath = Path.Combine(currentDir, oldName);

        if (File.Exists(oldPath))
        {
            try { File.Delete(oldPath); } catch { }
        }

        File.Move(currentExe, oldPath);

        string targetPath = Path.Combine(currentDir, currentName);
        if (File.Exists(targetPath))
        {
            try { File.Delete(targetPath); } catch { }
        }

        File.Move(newExePath, targetPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true
        });

        Application.Current.Exit();
        return "更新を適用しました。アプリを再起動します。";
    }
}
