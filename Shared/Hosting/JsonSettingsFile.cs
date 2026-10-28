using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;

namespace WinUiShared;

/// <summary>
/// settings.json などの JSON 設定ファイルの読み書き（全アプリ共通）。
/// 保存は「一時ファイルに全部書く → 本体と差し替える」の順にして、
/// 書き込み途中でクラッシュ・電源断が起きても、旧ファイルか新ファイルのどちらかが必ず完全な形で残るようにする。
/// </summary>
public static class JsonSettingsFile
{
    /// <summary>書き込み中の一時ファイルの拡張子（本体名 + .tmp）。</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>差し替え時の旧ファイル退避先（File.Replace が使う）。</summary>
    private const string BackupSuffix = ".bak";

    /// <summary>
    /// JSON を読み込む。ファイルが無い・壊れている・中身が null のときは null を返す（呼び出し側で既定値を作る）。
    /// </summary>
    public static T? Load<T>(string path, JsonSerializerSettings? serializerSettings = null) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return TryLoadBackup<T>(path, serializerSettings);

            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<T>(json, serializerSettings)
                ?? TryLoadBackup<T>(path, serializerSettings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"JsonSettingsFile.Load failed ({path}): {ex.Message}");
            // 本体が壊れていたら、差し替え時に残った 1 つ前の版で救済する
            return TryLoadBackup<T>(path, serializerSettings);
        }
    }

    private static T? TryLoadBackup<T>(string path, JsonSerializerSettings? serializerSettings) where T : class
    {
        try
        {
            string backup = path + BackupSuffix;
            if (!File.Exists(backup))
                return null;

            return JsonConvert.DeserializeObject<T>(File.ReadAllText(backup), serializerSettings);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// オブジェクトを JSON（インデント付き）にして安全に保存する。失敗しても例外は投げず false を返す
    /// （設定保存の失敗でアプリを落とさないため。旧実装も保存失敗は無視していた）。
    /// </summary>
    public static bool Save(string path, object value, JsonSerializerSettings? serializerSettings = null)
    {
        try
        {
            string json = JsonConvert.SerializeObject(value, Formatting.Indented, serializerSettings);
            WriteAllTextAtomic(path, json);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"JsonSettingsFile.Save failed ({path}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// テキストを一時ファイル経由で書き込み、本体と差し替える。
    /// File.WriteAllText で直接上書きすると、途中で止まったときに空や途中までのファイルが残る。
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tempPath = path + TempSuffix;

        // 1) 一時ファイルに全部書いてディスクへ確定させる（Flush(true) で OS のキャッシュも書き出す）
        //    File.WriteAllText と同じく BOM なし UTF-8 で書く
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        // 2) 本体と差し替える。既存があれば File.Replace（旧版は .bak に残す）、無ければ Move
        if (File.Exists(path))
        {
            try
            {
                File.Replace(tempPath, path, path + BackupSuffix, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // ネットワークドライブ等で Replace が使えない場合は上書き Move に切り替える
                Debug.WriteLine($"File.Replace failed, fallback to Move: {ex.Message}");
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
