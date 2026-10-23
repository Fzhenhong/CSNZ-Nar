namespace NarTool.Core;

/// <summary>打包选项。</summary>
public sealed class PackOptions
{
    /// <summary>输入目录，其内容会成为归档根。</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>
    /// 归档内根路径。留空时自动沿用解包清单里记录的原始路径，
    /// 因此"解包再打包"不需要人工填写。
    /// </summary>
    public string ArchiveRoot { get; init; }

    /// <summary>未命中清单时的默认存储方式。</summary>
    public NarStoreType StoreType { get; init; } = NarStoreType.EncodedAndCompressed;

    /// <summary>
    /// 源目录（或上级目录）存在 .nar-info.json 时，按清单还原每个条目的
    /// 原始路径与存储方式。关闭后所有文件统一使用 <see cref="StoreType"/>。
    /// </summary>
    public bool UseManifest { get; init; } = true;

    /// <summary>
    /// 还原模型的加密状态。清单记录了模型原本是否加密、是 20 还是 21 号密钥，
    /// 据此重新加密即可得到与原始归档一致的模型。
    /// </summary>
    public bool RestoreModelEncryption { get; init; } = true;

    /// <summary>扩展名 → 存储方式的覆盖规则，优先级高于清单。</summary>
    public Dictionary<string, NarStoreType> StoreTypeOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>打包时使用的临时目录。为空则使用系统临时目录。</summary>
    public string TempDirectory { get; init; }
}

/// <summary>
/// 目录 → NAR 的打包器。
///
/// 数据先顺序写入临时文件，目录表在最后统一压缩追加，因此打包大目录时
/// 内存占用稳定；写入完成前不会覆盖目标文件。
/// </summary>
public sealed class NarPacker
{
    private readonly PackOptions _options;
    private NarManifest _manifest;
    private List<PlannedFile> _planned = new();
    private int _completed;
    private int _total;

    /// <summary>单个文件的打包计划：本地路径、归档路径、数据区位置与目录表位置。</summary>
    private readonly record struct PlannedFile(string File, string Path, long DataOrder, int TableOrder);

    public NarPacker(PackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>本次打包实际使用的清单；为 null 表示未找到清单。</summary>
    public NarManifest Manifest => _manifest;

    /// <summary>清单中命中并据以还原的条目数。</summary>
    public int ManifestHits { get; private set; }

    /// <summary>
    /// 遍历 <see cref="PackOptions.SourceDirectory"/> 并打包为 <paramref name="outputPath"/>。
    /// 单个文件失败不会中断整个任务，结果汇总在返回的报告中。
    /// </summary>
    public TaskSummary Pack(string outputPath, IProgress<ProgressInfo> progress = null, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string source = Path.GetFullPath(_options.SourceDirectory);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("输入目录不存在：" + source);

        _manifest = _options.UseManifest ? NarManifest.FindNear(source) : null;

        var files = CollectFiles(source).ToList();
        _total = files.Count;
        _completed = 0;

        var summary = new TaskSummary { Total = files.Count };
        using var builder = new NarBuilder(_options.TempDirectory);

        string tempDirectory = null;
        try
        {
            // 需要加密的模型先复制到临时目录再处理，绝不改动用户的源文件
            bool needsCopy = _options.RestoreModelEncryption && files.Any(f => GameFileCrypt.IsModelPath(f));
            if (needsCopy)
                tempDirectory = Path.Combine(Path.GetTempPath(), "narpack_" + Guid.NewGuid().ToString("N"));

            _planned = PlanFor(source, files);
            foreach (string file in _planned.OrderBy(p => p.DataOrder).ThenBy(p => p.Path, StringComparer.Ordinal).Select(p => p.File))
            {
                if (cancellation.IsCancellationRequested)
                {
                    summary.Cancelled = true;
                    return summary;
                }

                string relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                var result = PackOne(builder, file, relative, tempDirectory);
                summary.Add(result);

                int done = Interlocked.Increment(ref _completed);
                progress?.Report(new ProgressInfo(done, _total, result.Path ?? relative));
            }

            if (summary.Failures.Count == 0)
                builder.Save(outputPath, true, _manifest != null ? Comparer<NarEntry>.Create(CompareByTableOrder) : null);
        }
        finally
        {
            if (tempDirectory != null && Directory.Exists(tempDirectory))
            {
                try { Directory.Delete(tempDirectory, recursive: true); }
                catch (IOException) { }
            }
        }

        return summary;
    }

    /// <summary>
    /// 生成打包计划。清单存在时分别还原数据区顺序（按原始偏移）与目录表顺序（按原始序号），
    /// 这样"解包再打包"的产物才能与原始归档逐字节一致。
    /// </summary>
    private List<PlannedFile> PlanFor(string source, List<string> files)
    {
        var planned = new List<PlannedFile>(files.Count);
        long unlistedData = long.MaxValue;

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            if (_manifest != null && _manifest.TryResolve(relative, out var known) && known != null)
            {
                planned.Add(new PlannedFile(file, known.Path, known.Off, known.Ord));
            }
            else
            {
                planned.Add(new PlannedFile(file, relative, unlistedData++, 0));
            }
        }

        return planned;
    }

    /// <summary>目录表排序：按清单记录的原始序号，未列出的排在后面。</summary>
    private int CompareByTableOrder(NarEntry a, NarEntry b)
    {
        int orderA = _manifest.TryResolve(a.Path.TrimStart('/'), out var ea) && ea != null ? ea.Ord : int.MaxValue;
        int orderB = _manifest.TryResolve(b.Path.TrimStart('/'), out var eb) && eb != null ? eb.Ord : int.MaxValue;
        int byOrder = orderA.CompareTo(orderB);
        return byOrder != 0 ? byOrder : string.CompareOrdinal(a.Path, b.Path);
    }

    private EntryResult PackOne(NarBuilder builder, string localPath, string relativePath, string tempDirectory)
    {
        string archivePath = relativePath;
        try
        {
            var info = new FileInfo(localPath);

            NarManifestEntry known = null;
            if (_manifest?.TryResolve(relativePath, out known) == true && known != null) ManifestHits++;

            // 路径：清单优先，其次显式 --root，最后用相对路径
            archivePath = known?.Path
                          ?? BuildArchivePath(relativePath);

            NarStoreType storeType = ResolveStoreType(localPath, known);
            string source = localPath;

            // 清单说这个模型原本是加密的，就加密回原样
            if (_options.RestoreModelEncryption && known?.Mdl is 20 or 21 && GameFileCrypt.IsModelPath(localPath))
            {
                string copy = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".mdl");
                Directory.CreateDirectory(tempDirectory);
                File.Copy(localPath, copy, overwrite: true);
                GameFileCrypt.TransformFile(copy, encrypt: true, known.Mdl);
                source = copy;
            }

            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var entry = builder.Add(archivePath, stream, storeType, info.LastWriteTime);
            return EntryResult.Ok(entry.Path, entry.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return EntryResult.Fail(archivePath, ex.Message);
        }
    }

    /// <summary>存储方式优先级：显式扩展名规则 &gt; 清单记录 &gt; 默认值。</summary>
    private NarStoreType ResolveStoreType(string localPath, NarManifestEntry known)
    {
        string extension = Path.GetExtension(localPath);
        if (_options.StoreTypeOverrides != null && !string.IsNullOrEmpty(extension)
            && _options.StoreTypeOverrides.TryGetValue(extension, out var overridden))
        {
            return overridden;
        }

        if (known != null && Enum.IsDefined(typeof(NarStoreType), known.Store))
            return (NarStoreType)known.Store;

        return _options.StoreType;
    }

    private string BuildArchivePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(_options.ArchiveRoot))
            return NarBuilder.NormalizePath(relativePath);
        return NarBuilder.NormalizePath(_options.ArchiveRoot.Trim('/') + "/" + relativePath);
    }

    /// <summary>递归枚举文件，跳过清单文件自身。</summary>
    private static IEnumerable<string> CollectFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string directory = stack.Pop();

            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(directory);
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                if (string.Equals(Path.GetFileName(file), NarManifest.FileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return file;
            }

            Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);
            for (int i = subdirectories.Length - 1; i >= 0; i--) stack.Push(subdirectories[i]);
        }
    }
}
