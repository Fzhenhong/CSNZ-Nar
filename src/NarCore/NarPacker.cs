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

    /// <summary>
    /// 并行压缩线程数。默认按 CPU 核心数自动取值。
    /// 压缩是打包的主要开销，线程越多越快；受内存上限约束。
    /// </summary>
    public int Threads { get; init; } = DefaultThreads;

    /// <summary>
    /// 并行压缩阶段允许占用的内存上限（MB）。批次大小据此动态收缩，
    /// 保证在内存较小的机器上也不会 OOM。单条目超过此值时单独成批。
    /// </summary>
    public int MemoryBudgetMB { get; init; } = 512;

    /// <summary>
    /// 单批最多同时持有的文件数。批次越大内存利用越充分，但会一次性占用大量文件句柄，
    /// 在文件极多时造成启动阶段的卡顿。取线程数的若干倍即可让 CPU 保持饱和。
    /// </summary>
    public int MaxBatchFiles => Environment.ProcessorCount;

    private static int DefaultThreads => Math.Clamp(Environment.ProcessorCount, 1, 32);
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

        // 先给 UI 一个即时反馈：后续要枚举目录、读取清单、统计文件，
        // 这些在超大目录上需要几百毫秒，若没有进度会表现为"点了没反应"。
        progress?.Report(new ProgressInfo(0, 1, "正在扫描目录..."));

        _manifest = _options.UseManifest ? NarManifest.FindNear(source) : null;

        progress?.Report(new ProgressInfo(0, 1, "正在枚举文件..."));

        var files = CollectFiles(source).ToList();
        _total = files.Count;
        _completed = 0;

        progress?.Report(new ProgressInfo(0, Math.Max(_total, 1), $"共 {_total} 个文件，准备打包..."));

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

            // 按数据区顺序遍历，分批并行编码，再按批内顺序落盘
            var ordered = _planned
                .OrderBy(p => p.DataOrder)
                .ThenBy(p => p.Path, StringComparer.Ordinal)
                .Select(p => new Job
                {
                    File = p.File,
                    Relative = Path.GetRelativePath(source, p.File).Replace(Path.DirectorySeparatorChar, '/'),
                })
                .ToList();

            foreach (var job in ordered)
            {
                job.Info = new FileInfo(job.File);
                _manifest?.TryResolve(job.Relative, out job.Known);
                job.ArchivePath = ResolveArchivePath(job.Relative);
                job.StoreType = ResolveStoreType(job.File, job.Relative);
            }

            long budget = Math.Max(32, _options.MemoryBudgetMB) * 1024L * 1024L;
            int index = 0;
            while (index < ordered.Count)
            {
                if (cancellation.IsCancellationRequested)
                {
                    summary.Cancelled = true;
                    return summary;
                }

                // 按预算切批：估算为文件大小（编码后通常更小，估算偏保守是安全的）
                var batch = new List<Job>();
                long batchBytes = 0;
                int batchLimit = Math.Max(_options.MaxBatchFiles, _options.Threads * 4);
                while (index < ordered.Count && batch.Count < batchLimit)
                {
                    var candidate = ordered[index];
                    long size = candidate.Info.Exists ? candidate.Info.Length : 0;
                    if (batch.Count > 0 && batchBytes + size > budget) break;
                    batch.Add(candidate);
                    batchBytes += size;
                    index++;
                }

                RunBatch(builder, batch, tempDirectory, summary, progress, cancellation);
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

    /// <summary>单个待打包文件的全部前置信息，预先算好以便并行阶段无共享状态。</summary>
    private sealed class Job
    {
        public string File;
        public string Relative;
        public string ArchivePath;
        public NarStoreType StoreType;
        public FileInfo Info;
        public string TempCopy;
        public NarManifestEntry Known;
    }

    /// <summary>
    /// 执行一批：先并行编码（纯 CPU、无共享状态），再按批内顺序提交落盘。
    /// 打不开的文件直接记为失败并排除出本批，绝不会写成空条目。
    /// </summary>
    private void RunBatch(NarBuilder builder, List<Job> batch, string tempDirectory,
        TaskSummary summary, IProgress<ProgressInfo> progress, CancellationToken cancellation)
    {
        var opened = new List<FileStream>(batch.Count);
        var ready = new List<Job>(batch.Count);
        var pending = new List<NarBuilder.PendingEntry>(batch.Count);
        var failures = new (Job Job, string Error)[batch.Count];
        var streams = new FileStream[batch.Count];

        try
        {
            // 并行打开本批文件：批次可能上千个，串行打开会在启动时形成明显停顿
            Parallel.For(0, batch.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.Threads),
                CancellationToken = cancellation,
            }, i =>
            {
                try
                {
                    streams[i] = OpenContent(batch[i], tempDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or NotSupportedException or ArgumentException)
                {
                    failures[i] = (batch[i], ex.Message);
                }
            });

            // 按原顺序收集成功项，保证落盘顺序稳定
            for (int i = 0; i < batch.Count; i++)
            {
                if (streams[i] == null)
                {
                    var (failedJob, error) = failures[i];
                    summary.Add(EntryResult.Fail(failedJob.ArchivePath, error ?? "无法打开文件。"));
                    _completed++;
                    continue;
                }

                opened.Add(streams[i]);
                ready.Add(batch[i]);
                pending.Add(new NarBuilder.PendingEntry
                {
                    ArchivePath = batch[i].ArchivePath,
                    Content = streams[i],
                    StoreType = batch[i].StoreType,
                    LastModified = batch[i].Info.LastWriteTime,
                });
            }

            if (pending.Count > 0)
            {
                int baseDone = _completed;
                var inner = progress == null
                    ? null
                    : new CallbackProgress(n => progress.Report(
                        new ProgressInfo(baseDone + n, _total, ready[Math.Min(n, ready.Count) - 1].ArchivePath)));

                builder.AddRange(pending, _options.Threads, inner, cancellation);

                foreach (var job in ready)
                    summary.Add(EntryResult.Ok(job.ArchivePath, job.Info.Length));
                _completed = baseDone + ready.Count;
            }
        }
        finally
        {
            foreach (var stream in opened)
            {
                try { stream.Dispose(); } catch (IOException) { }
            }
            foreach (var job in batch)
            {
                if (job.TempCopy == null) continue;
                try { File.Delete(job.TempCopy); } catch (IOException) { }
            }
        }
    }

    /// <summary>打开条目内容。清单要求加密的模型先复制到临时目录再加密，绝不改动用户源文件。</summary>
    private FileStream OpenContent(Job job, string tempDirectory)
    {
        string source = job.File;

        if (_options.RestoreModelEncryption && job.Known?.Mdl is 20 or 21 && GameFileCrypt.IsModelPath(job.File))
        {
            Directory.CreateDirectory(tempDirectory);
            string copy = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".mdl");
            File.Copy(job.File, copy, overwrite: true);
            GameFileCrypt.TransformFile(copy, encrypt: true, job.Known.Mdl);
            job.TempCopy = copy;
            source = copy;
        }

        return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    }

    /// <summary>按清单或 --root 决定归档内路径。</summary>
    private string ResolveArchivePath(string relative)
    {
        if (_manifest != null && _manifest.TryResolve(relative, out var known) && known != null)
        {
            ManifestHits++;
            return known.Path;
        }
        return BuildArchivePath(relative);
    }

    /// <summary>存储方式优先级：显式扩展名规则 &gt; 清单记录 &gt; 默认值。</summary>
    private NarStoreType ResolveStoreType(string localPath, string relative)
    {
        string extension = Path.GetExtension(localPath);
        if (_options.StoreTypeOverrides != null && !string.IsNullOrEmpty(extension)
            && _options.StoreTypeOverrides.TryGetValue(extension, out var overridden))
        {
            return overridden;
        }

        if (_manifest != null && _manifest.TryResolve(relative, out var known) && known != null
            && Enum.IsDefined(typeof(NarStoreType), known.Store))
        {
            return (NarStoreType)known.Store;
        }

        return _options.StoreType;
    }

    private string BuildArchivePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(_options.ArchiveRoot))
            return NarBuilder.NormalizePath(relativePath);
        return NarBuilder.NormalizePath(_options.ArchiveRoot.Trim('/') + "/" + relativePath);
    }

    /// <summary>把进度回调包装成"本批已完成数"，供 AddRange 使用。</summary>
    private sealed class CallbackProgress : IProgress<ProgressInfo>
    {
        private readonly Action<int> _handler;
        public CallbackProgress(Action<int> handler) => _handler = handler;
        public void Report(ProgressInfo value) => _handler(value.Current);
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
