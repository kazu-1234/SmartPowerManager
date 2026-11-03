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
    /// 読み込みの既定設定。リスト・配列のプロパティだけ「保存値で置き換える」にする。
    /// Newtonsoft の既定（Reuse）だと、コンストラクタで既定値を入れたリストに保存値が「追加」され、
    /// 保存と読み込みのたびに要素が増えていく（以前は SunriseWatcher / MusicPlayer だけが個別に避けていた）。
    /// 辞書は従来どおり足し合わせる（新しい版で増えた既定のキーを、既存の設定にも取り込めるようにするため）。
    /// </summary>
    private static readonly JsonSerializerSettings DefaultLoadSettings = new()
    {
        ContractResolver = new ReplaceListsContractResolver()
    };

    /// <summary>
    /// 保存の排他（パスごと）。一時ファイル名が固定なので、同じファイルを同時に保存すると片方が黙って失敗していた。
    /// キーは大文字小文字を区別しない（Windows のパスのため）。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> SaveLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// JSON を読み込む。ファイルが無い・壊れている・中身が null のときは null を返す（呼び出し側で既定値を作る）。
    /// serializerSettings を渡さないときは、リストを置き換える既定設定を使う。
    /// </summary>
    public static T? Load<T>(string path, JsonSerializerSettings? serializerSettings = null) where T : class
    {
        serializerSettings ??= DefaultLoadSettings;
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
            // 同じファイルへの保存は 1 つずつ（UI スレッドと監視タイマーなどから同時に呼ばれても壊さない）
            lock (SaveLocks.GetOrAdd(Path.GetFullPath(path), _ => new object()))
            {
                WriteAllTextAtomic(path, json);
            }
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

    /// <summary>
    /// リスト・配列（辞書以外のコレクション）のプロパティだけ ObjectCreationHandling.Replace にする。
    /// 個別に [JsonProperty(ObjectCreationHandling = ...)] が付いているプロパティは、その指定を優先する。
    /// </summary>
    private sealed class ReplaceListsContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        protected override Newtonsoft.Json.Serialization.JsonProperty CreateProperty(
            System.Reflection.MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            Type? type = property.PropertyType;
            // 書き込めない（getter だけの）プロパティは置き換えられないので、従来どおり既存のリストへ読み込む
            if (property.ObjectCreationHandling == null && property.Writable && type != null && type != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
                && !typeof(System.Collections.IDictionary).IsAssignableFrom(type)
                && !IsGenericDictionary(type))
            {
                property.ObjectCreationHandling = ObjectCreationHandling.Replace;
            }

            return property;
        }

        private static bool IsGenericDictionary(Type type) =>
            type.GetInterfaces().Append(type).Any(t =>
                t.IsGenericType
                && (t.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
    }
}
