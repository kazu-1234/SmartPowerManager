namespace WinUiShared;

/// <summary>
/// %AppData%\&lt;appFolderName&gt;\lifetime.log へのイベントログ。
/// 重要イベントのみを想定。1MB 超で末尾側を残してトリムする。
/// </summary>
public static class LifetimeLog
{
    private const string FileName = "lifetime.log";
    private const long MaxBytes = 1 * 1024 * 1024;
    private const int KeepBytes = 750 * 1024;

    public static void Append(string appFolderName, string message)
    {
        if (string.IsNullOrWhiteSpace(appFolderName))
            return;

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                appFolderName);
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, FileName);
            File.AppendAllText(path, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
            TrimIfNeeded(path);
        }
        catch
        {
        }
    }

    private static void TrimIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes)
                return;

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length <= KeepBytes)
                return;

            int start = bytes.Length - KeepBytes;
            while (start < bytes.Length && bytes[start] != (byte)'\n')
                start++;
            if (start < bytes.Length)
                start++;

            if (start >= bytes.Length)
                return;

            File.WriteAllBytes(path, bytes[start..]);
        }
        catch
        {
        }
    }
}
