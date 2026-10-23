using System.Collections.Concurrent;

namespace NarTool.Core;

/// <summary>解包选项。</summary>
public sealed class ExtractOptions
{
    /// <summary>输出目录。目录结构按归档内路径还原。</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>解包 .mdl 时顺带解除 ICE 加密（默认开启，加密模型占少数但必须解）。</summary>
    public bool DecryptModels { get; init; } = true;

    /// <summary>
    /// 在输出目录写出 .nar-info.json，记录归档根、每个条目的存储方式与模型原始版本。
    /// 打包时自动读回，用户无需手填 --root，也无需记住哪个模型原来是 21 号加密。
    /// </summary>
    public bool WriteManifest { get; init; } = true;

    /// <summary>解出后按目录表 CRC32 校验；不一致记为失败。</summary>
    public bool VerifyChecksum { get; init; }

    /// <summary>已存在同名文件时覆盖；为 false 则跳过。</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>并发解包的线程数。读盘随机访问、写盘顺序，4~8 通常最优。</summary>
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>不依赖文件系统路径的非法字符，统一替换以免解包中断。</summary>
    public bool SanitizePaths { get; init; } = true;
}

/// <summary>
/// 多线程解包器。每个线程独立打开数据文件并按偏移随机读取，
/// 因此内存占用与归档大小无关，2 GB 以上的 NAR 也不会爆内存。
/// </summary>
public sealed class NarExtractor
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    private readonly NarArchive _archive;
    private readonly ExtractOptions _options;
    private readonly NarManifest _manifest = new();
    private Dictionary<NarEntry, int> _order = new();
    private readonly object _manifestLock = new();
    private int _completed;
    private int _total;

    public NarExtractor(NarArchive archive, ExtractOptions options)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// 解包指定条目。<paramref name="progress"/> 按完成条目数回调，
    /// <paramref name="cancellation"/> 取消后立即停止（已写出的文件保留）。
    /// </summary>
    public TaskSummary Extract(
        IEnumerable<NarEntry> entries,
        IProgress<ProgressInfo> progress = null,
        CancellationToken cancellation = default)
    {
        var list = entries.ToList();
        var summary = new TaskSummary { Total = list.Count };
        _total = list.Count;
        _completed = 0;

        Directory.CreateDirectory(_options.OutputDirectory);

        // 记下条目在原始目录表中的位置，供打包时还原顺序
        _order = new Dictionary<NarEntry, int>(list.Count);
        for (int i = 0; i < list.Count; i++) _order[list[i]] = i;

        var queue = new ConcurrentQueue<NarEntry>(list);
        int threadCount = Math.Max(1, Math.Min(_options.Threads, list.Count));

        var workers = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            workers[i] = Task.Run(() =>
            {
                using var data = new NarDataFile(_archive.FilePath);
                while (!cancellation.IsCancellationRequested && queue.TryDequeue(out var entry))
                {
                    var result = ExtractOne(data, entry);
                    summary.Add(result);

                    int done = Interlocked.Increment(ref _completed);
                    progress?.Report(new ProgressInfo(done, _total, entry.Path));
                }
            }, cancellation);
        }

        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // 取消是预期路径
        }

        summary.Cancelled = cancellation.IsCancellationRequested;

        if (_options.WriteManifest)
        {
            lock (_manifestLock)
            {
                _manifest.Source = _archive.FilePath;
                // 清单只是便利功能，写失败不影响已经解出的文件
                _manifest.TrySave(_options.OutputDirectory);
            }
        }

        return summary;
    }

    private void Remember(NarEntry entry, int? encryptedVersion)
    {
        if (!_options.WriteManifest) return;
        lock (_manifestLock)
        {
            _manifest.Entries.Add(new NarManifestEntry
            {
                Path = entry.Path,
                Store = (int)entry.StoreType,
                Mdl = encryptedVersion,
                // 目录表顺序是原始归档的一部分，记录后才能在打包时精确还原
                Ord = _order.TryGetValue(entry, out int index) ? index : _manifest.Entries.Count,
                Off = entry.Offset,
            });
        }
    }

    private EntryResult ExtractOne(NarDataFile data, NarEntry entry)
    {
        string target = null;
        try
        {
            target = ResolveTargetPath(entry);
            if (target == null)
                return EntryResult.Fail(entry.Path, "路径为空或仅包含目录分隔符。");

            if (!_options.Overwrite && File.Exists(target))
                return EntryResult.Ok(entry.Path, 0);

            string directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            long written;
            int? encryptedVersion = null;
            using (var output = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                written = _archive.Extract(entry, output);
                output.Flush();

                if (_options.DecryptModels && GameFileCrypt.IsModelPath(entry.Path))
                {
                    // 先记下原版本号，加密模型解密后会变成 9，这个信息就此丢失
                    int? version = GameFileCrypt.ReadModelVersion(output);
                    if (version is 20 or 21)
                    {
                        encryptedVersion = version;
                        GameFileCrypt.Transform(output, encrypt: false);
                    }
                }
            }

            if (_options.VerifyChecksum && !_archive.Verify(entry))
                return EntryResult.Fail(entry.Path, "CRC32 校验和不匹配，数据可能已损坏。");

            ApplyTimestamp(target, entry.LastModified);
            Remember(entry, encryptedVersion);
            return EntryResult.Ok(entry.Path, written);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            TryDelete(target);
            return EntryResult.Fail(entry.Path, ex.Message);
        }
    }

    /// <summary>把归档内路径映射为本地绝对路径，并阻断跳出输出目录的路径。</summary>
    private string ResolveTargetPath(NarEntry entry)
    {
        string relative = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        relative = relative.TrimStart(Path.DirectorySeparatorChar);
        if (relative.Length == 0) return null;

        string root = Path.GetFullPath(_options.OutputDirectory);
        string target = Path.GetFullPath(Path.Combine(root, relative));

        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("条目路径试图写到输出目录之外：" + entry.Path);

        if (_options.SanitizePaths)
        {
            string directory = Path.GetDirectoryName(target);
            string name = SanitizeFileName(Path.GetFileName(target));
            if (!string.IsNullOrEmpty(directory)) target = Path.Combine(directory, name);
        }

        return target;
    }

    private static string SanitizeFileName(string name)
    {
        if (name.IndexOfAny(InvalidChars) < 0) return name;
        foreach (char c in InvalidChars) name = name.Replace(c, '_');
        return name;
    }

    private static void ApplyTimestamp(string path, DateTime timestamp)
    {
        try
        {
            if (timestamp.Year is >= 1980 and <= 2107)
                File.SetLastWriteTime(path, timestamp);
        }
        catch (IOException)
        {
            // 时间戳失败不影响内容正确性
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        if (path == null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
