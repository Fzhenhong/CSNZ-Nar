using System.Text.Json;
using System.Text.Json.Serialization;

namespace NarTool.Core;

/// <summary>清单中的单个条目，记录解包时观察到的原始状态。</summary>
public sealed class NarManifestEntry
{
    /// <summary>归档内的完整路径，如 "/fixtrike/models/a.mdl"。</summary>
    public string Path { get; set; }

    /// <summary>原始存储类型。打包时按原样还原，保证产物与原归档同构。</summary>
    public int Store { get; set; }

    /// <summary>解密前的模型版本（20 或 21）。null 表示该文件未加密。</summary>
    public int? Mdl { get; set; }

    /// <summary>在原始目录表中的序号。打包时据此还原目录表顺序。</summary>
    public int Ord { get; set; }

    /// <summary>原始数据区偏移。打包时据此还原数据块排列，使产物与原归档逐字节一致。</summary>
    public long Off { get; set; }

    /// <summary>去掉前导 '/' 的相对路径，用于与本地文件匹配。</summary>
    [JsonIgnore]
    public string RelativePath => Path.StartsWith('/') ? Path.Substring(1) : Path;
}

/// <summary>
/// 解包清单。解包时随文件一起写出，打包时自动读回，
/// 因此归档内的根目录、每个文件的存储方式、以及模型原本的加密版本
/// 都不需要人工指定。
/// </summary>
public sealed class NarManifest
{
    /// <summary>清单文件名。以点开头，避免干扰目录浏览。</summary>
    public const string FileName = ".nar-info.json";

    private const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>来源归档的完整路径，仅作溯源参考。</summary>
    public string Source { get; set; }

    public DateTime Created { get; set; } = DateTime.Now;

    public List<NarManifestEntry> Entries { get; set; } = new();

    private Dictionary<string, NarManifestEntry> _index;
    private readonly object _indexLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>在指定目录写出清单。失败不抛异常：清单只是便利功能，不应影响解包结果。</summary>
    public bool TrySave(string directory)
    {
        try
        {
            Entries.Sort((a, b) => a.Ord.CompareTo(b.Ord));
            string target = Path.Combine(directory, FileName);
            File.WriteAllText(target, JsonSerializer.Serialize(this, SerializerOptions), System.Text.Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>从目录读取清单；不存在或格式不合法时返回 null。</summary>
    public static NarManifest TryLoad(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        string file = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(file)) return null;
            var manifest = JsonSerializer.Deserialize<NarManifest>(File.ReadAllText(file), SerializerOptions);
            if (manifest?.Entries == null || manifest.Version != CurrentVersion) return null;
            manifest._index = null;
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 在源目录及其上级目录中寻找清单。用户既可能选 <c>解包目录</c>，
    /// 也可能选 <c>解包目录/fixtrike</c>，两种情况都应能找到。
    /// </summary>
    public static NarManifest FindNear(string directory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        for (int depth = 0; depth < 3 && current != null; depth++)
        {
            var manifest = TryLoad(current.FullName);
            if (manifest != null) return manifest;
            current = current.Parent;
        }
        return null;
    }

    private Dictionary<string, NarManifestEntry> Index()
    {
        if (_index != null) return _index;
        lock (_indexLock)
        {
            if (_index == null)
            {
                var index = new Dictionary<string, NarManifestEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in Entries)
                {
                    if (string.IsNullOrEmpty(entry.Path)) continue;
                    index[entry.RelativePath] = entry;
                }
                _index = index;
            }
            return _index;
        }
    }

    /// <summary>
    /// 按本地相对路径查找对应条目。先精确匹配；再退化为"以该相对路径结尾"，
    /// 以兼容把源目录选成归档根子目录的情况。
    /// </summary>
    public bool TryResolve(string relativeLocalPath, out NarManifestEntry entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(relativeLocalPath)) return false;

        string key = relativeLocalPath.Replace('\\', '/').TrimStart('/');
        if (Index().TryGetValue(key, out entry) && entry != null) return true;

        string suffix = "/" + key;
        foreach (var candidate in Entries)
        {
            if (candidate.RelativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }
        }
        entry = null;
        return false;
    }
}
