using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace NarTool.Core;

/// <summary>
/// NAR 归档写入器。
///
/// 写入分两段：先并行把每个条目编码/压缩到内存缓冲，再按顺序落盘。
/// 压缩是打包的主要开销，并行后能随 CPU 核心数扩展；
/// 单批内存由 <see cref="AddRange"/> 的分批策略约束，与归档总大小无关。
/// 生成的文件在写入完成前不会触碰目标路径。
/// </summary>
public sealed class NarBuilder : IDisposable
{
    private readonly List<NarEntry> _entries = new();
    private readonly FileStream _data;
    private readonly string _tempPath;
    private bool _disposed;
    private long _position;

    public NarBuilder(string tempDirectory = null)
    {
        _tempPath = Path.Combine(tempDirectory ?? Path.GetTempPath(), "narbuild_" + Guid.NewGuid().ToString("N") + ".tmp");
        _data = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        WriteData(BuildFileHeader());
        _position = NarFormat.DataStart;
    }

    public IReadOnlyList<NarEntry> Entries => _entries;

    public int Count => _entries.Count;

    public long DataSize => _position;

    private static byte[] BuildFileHeader()
    {
        byte[] header = new byte[NarFormat.DataStart];
        BitConverter.GetBytes(NarFormat.Signature).CopyTo(header, 0);
        BitConverter.GetBytes(NarFormat.ContainerVersion).CopyTo(header, 4);
        return header;
    }

    /// <summary>把归档路径规范化为以 '/' 开头的形式，并校验非空。</summary>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//")) p = p.Replace("//", "/");
        if (p is "/" or "") throw new ArgumentException("归档路径不能为空。", nameof(path));
        return p;
    }

    /// <summary>待写入的条目描述，供批量并行写入使用。</summary>
    public sealed class PendingEntry
    {
        public required string ArchivePath { get; init; }
        public required Stream Content { get; init; }
        public required NarStoreType StoreType { get; init; }
        public required DateTime LastModified { get; init; }
    }

    /// <summary>追加单个条目（内容读尽后不释放调用方的流）。</summary>
    public NarEntry Add(string archivePath, Stream content, NarStoreType storeType, DateTime lastModified)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string path = NormalizePath(archivePath);
        var encoded = Encode(path, content, storeType);
        return Commit(encoded, storeType, lastModified);
    }

    /// <summary>
    /// 并行追加一批条目。编码按 CPU 并行执行，落盘严格按 items 的顺序，
    /// 因此产物与逐条同步追加完全一致。
    /// </summary>
    public void AddRange(IReadOnlyList<PendingEntry> items, int threads,
        IProgress<ProgressInfo> progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (items.Count == 0) return;

        threads = Math.Clamp(threads, 1, Math.Max(1, Environment.ProcessorCount * 2));
        var encoded = new EncodedEntry[items.Count];

        // 捕获首个异常，其余并行分支尽快退出
        Exception firstError = null;
        var errorLock = new object();
        int completed = 0;

        try
        {
            Parallel.For(0, items.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = threads,
                CancellationToken = cancellation,
            }, i =>
            {
                var item = items[i];
                try
                {
                    if (Volatile.Read(ref firstError) == null)
                        encoded[i] = Encode(NormalizePath(item.ArchivePath), item.Content, item.StoreType);
                }
                catch (Exception ex)
                {
                    lock (errorLock) firstError ??= ex;
                }
                finally
                {
                    int n = Interlocked.Increment(ref completed);
                    progress?.Report(new ProgressInfo(n, items.Count, item.ArchivePath));
                }
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (firstError != null) throw firstError;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            Commit(encoded[i], item.StoreType, item.LastModified);
        }
    }

    private sealed class EncodedEntry
    {
        public required string Path { get; init; }
        public required ReadOnlyMemory<byte> Data { get; init; }
        public required long OriginalSize { get; init; }
        public required uint Checksum { get; init; }
    }

    /// <summary>
    /// 把条目内容编码为最终存储字节（纯内存、无共享状态，可安全并行）。
    /// 校验和覆盖最终写出的存储字节，与客户端 Verify 行为一致。
    /// </summary>
    private static EncodedEntry Encode(string path, Stream content, NarStoreType storeType)
    {
        byte[] key = storeType == NarStoreType.Raw ? null : EntryKey.Create(path);
        var output = new GrowBuffer();
        var crc = new Crc32();
        long originalSize = 0;

        switch (storeType)
        {
            case NarStoreType.Raw:
            {
                originalSize = Pump(content, output, crc);
                break;
            }
            case NarStoreType.Encoded:
            {
                // 直接对明文做 16 字节循环异或，同样累加 CRC
                originalSize = PumpXor(content, output, crc, key);
                break;
            }
            case NarStoreType.EncodedAndCompressed:
            {
                // 存储形式是 XOR(LZ(明文))：先压缩，再对压缩结果异或，并对异或后的字节求 CRC
                using var compressor = new GrowBufferStream();
                // Compress 返回读取的原始字节数，正好是目录表需要的 size
                originalSize = NarLzCodec.Compress(content, compressor);
                XorInto(compressor.Buffer.Span, output, crc, key);
                break;
            }
            default:
                throw new NotSupportedException($"不支持的存储类型 {(int)storeType}。");
        }

        return new EncodedEntry
        {
            Path = path,
            Data = output.ToMemory(),
            OriginalSize = originalSize,
            Checksum = crc.Value,
        };
    }

    private const int ChunkSize = 128 * 1024;

    private static long Pump(Stream source, GrowBuffer destination, Crc32 crc)
    {
        var chunk = new byte[ChunkSize];
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            var span = chunk.AsSpan(0, read);
            crc.Update(span);
            destination.Append(span);
            total += read;
        }
        return total;
    }

    private static long PumpXor(Stream source, GrowBuffer destination, Crc32 crc, byte[] key)
    {
        var chunk = new byte[ChunkSize];
        var vector = KeyXor.BuildVector(key);
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            var span = chunk.AsSpan(0, read);
            KeyXor.Apply(span, key, (int)(total & 0xF), vector);
            crc.Update(span);
            destination.Append(span);
            total += read;
        }
        return total;
    }

    /// <summary>
    /// 把整段压缩数据异或后追加到目标，并累计 CRC。
    /// 直接借用调用方的跨度，不额外复制。
    /// </summary>
    private static void XorInto(ReadOnlySpan<byte> source, GrowBuffer destination, Crc32 crc, byte[] key)
    {
        if (source.IsEmpty) return;

        // 分块处理，避免对大条目再分配一份等长缓冲
        var vector = KeyXor.BuildVector(key);
        int offset = 0;
        while (offset < source.Length)
        {
            int size = Math.Min(ChunkSize, source.Length - offset);
            var dest = destination.Reserve(size);
            source.Slice(offset, size).CopyTo(dest);
            KeyXor.Apply(dest, key, offset & 0xF, vector);
            crc.Update(dest);
            destination.Advance(size);
            offset += size;
        }
    }

    /// <summary>把编码结果落盘并登记目录项。</summary>
    private NarEntry Commit(EncodedEntry encoded, NarStoreType storeType, DateTime lastModified)
    {
        long offset = _position;

        if (encoded.Data.Length > int.MaxValue)
            throw new InvalidOperationException($"条目过大（{encoded.Data.Length} 字节），NAR 目录表无法记录：{encoded.Path}");
        if (encoded.OriginalSize > int.MaxValue)
            throw new InvalidOperationException($"条目过大（{encoded.OriginalSize} 字节），NAR 目录表无法记录：{encoded.Path}");

        var data = encoded.Data.Span;
        _data.Write(data);
        _position += data.Length;

        var entry = new NarEntry
        {
            Path = encoded.Path,
            StoreType = storeType,
            Offset = offset,
            StoreSize = encoded.Data.Length,
            Size = encoded.OriginalSize,
            LastModified = lastModified,
            Checksum = encoded.Checksum,
        };
        _entries.Add(entry);
        return entry;
    }

    private void WriteData(byte[] buffer) => _data.Write(buffer, 0, buffer.Length);

    /// <summary>
    /// 打包目录表，压缩后追加到数据末尾，再把临时文件移到目标位置。
    /// <paramref name="tableOrder"/> 用于重排目录表中的条目：数据区按写入顺序落盘，
    /// 目录表可另按一顺序排列，以便复现原归档布局。
    /// </summary>
    public void Save(string outputPath, bool overwrite = true, IComparer<NarEntry> tableOrder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (tableOrder != null) _entries.Sort(tableOrder);

        byte[] table = BuildTable();
        byte[] compressed;
        using (var input = new MemoryStream(table, writable: false))
        using (var output = new MemoryStream())
        {
            BZip2.Compress(input, output, false, 1);
            compressed = output.ToArray();
        }
        for (int i = 0; i < compressed.Length; i++)
            compressed[i] ^= NarFormat.TableXor[i & 0xF];

        WriteData(compressed);
        WriteData(BuildFooter(compressed.Length));
        _data.Flush(true);

        string target = Path.GetFullPath(outputPath);
        string directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _data.Dispose();
        File.Move(_tempPath, target, overwrite);
        _disposed = true;
    }

    private byte[] BuildFooter(int compressedLength)
    {
        byte[] footer = new byte[NarFormat.FooterSize];
        BitConverter.GetBytes(compressedLength ^ NarFormat.LengthXorKey).CopyTo(footer, 0);
        BitConverter.GetBytes(NarFormat.Signature).CopyTo(footer, 4);
        return footer;
    }

    private byte[] BuildTable()
    {
        long estimated = NarFormat.HeaderPreambleSize;
        foreach (var e in _entries) estimated += 2 + Encoding.UTF8.GetByteCount(e.Path) + NarFormat.EntryFixedSize;
        if (estimated > int.MaxValue)
            throw new InvalidOperationException("条目过多，NAR 目录表超出可表示范围。");

        using var buffer = new MemoryStream((int)estimated);
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        writer.Write(NarFormat.HeaderVersion);
        foreach (int field in NarFormat.PreambleFields) writer.Write(field);
        writer.Write(_entries.Count);

        foreach (var e in _entries)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(e.Path);
            writer.Write((ushort)pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write((int)e.StoreType);
            writer.Write((uint)e.Offset);
            writer.Write((int)e.StoreSize);
            writer.Write((int)e.Size);
            writer.Write((int)new DateTimeOffset(DateTime.SpecifyKind(e.LastModified, DateTimeKind.Local)).ToUnixTimeSeconds());
            writer.Write(e.Checksum);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _data.Dispose();
        try
        {
            if (File.Exists(_tempPath)) File.Delete(_tempPath);
        }
        catch (IOException)
        {
            // 临时文件残留不影响正确性，交给系统清理
        }
    }
}
