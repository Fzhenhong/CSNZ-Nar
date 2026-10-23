using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace NarTool.Core;

/// <summary>
/// NAR 归档写入器。
///
/// 数据区先写入临时文件，目录表在最后统一压缩追加，因此打包
/// 2 GB 级别的归档时内存占用也只有几十 MB。写入完成前不会触碰目标文件。
/// </summary>
public sealed class NarBuilder : IDisposable
{
    private readonly List<NarEntry> _entries = new();
    private readonly FileStream _data;
    private readonly string _tempPath;
    private bool _disposed;

    /// <summary>
    /// 逻辑写入位置。不能用 FileStream.Length —— 它只反映已落盘的字节，
    /// 缓冲区里的数据不计入，会造成条目偏移错误。
    /// </summary>
    private long _position;

    public NarBuilder(string tempDirectory = null)
    {
        _tempPath = Path.Combine(tempDirectory ?? Path.GetTempPath(), "narbuild_" + Guid.NewGuid().ToString("N") + ".tmp");
        _data = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        WriteData(BuildFileHeader());
        _position = NarFormat.DataStart;
    }

    public IReadOnlyList<NarEntry> Entries => _entries;

    /// <summary>归档内路径条数。</summary>
    public int Count => _entries.Count;

    /// <summary>数据区已写入的字节数（不含目录表）。</summary>
    public long DataSize => _position;

    private static byte[] BuildFileHeader()
    {
        byte[] header = new byte[NarFormat.DataStart];
        BitConverter.GetBytes(NarFormat.Signature).CopyTo(header, 0);
        BitConverter.GetBytes(NarFormat.ContainerVersion).CopyTo(header, 4);
        return header;
    }

    /// <summary>把归档路径规范化为以 '/' 开头的形式，并校验为空路径。</summary>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//")) p = p.Replace("//", "/");
        if (p is "/" or "") throw new ArgumentException("归档路径不能为空。", nameof(path));
        return p;
    }

    /// <summary>
    /// 追加一个条目。<paramref name="content"/> 从头读到尾后不会被释放。
    /// </summary>
    public NarEntry Add(string archivePath, Stream content, NarStoreType storeType, DateTime lastModified)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string path = NormalizePath(archivePath);
        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        if (pathBytes.Length > ushort.MaxValue)
            throw new ArgumentException($"路径过长（{pathBytes.Length} 字节）：{path}", nameof(archivePath));

        long offset = _position;
        uint checksum;
        long originalSize = 0;

        switch (storeType)
        {
            case NarStoreType.Raw:
            {
                var tee = new Crc32TeeStream(content);
                originalSize = CopyThrough(tee, _data);
                _position += originalSize;
                checksum = tee.Checksum;
                break;
            }
            case NarStoreType.Encoded:
            {
                var encoded = new Crc32TeeStream(new XorKeyStream(content, EntryKey.Create(pathBytes)));
                originalSize = CopyThrough(encoded, _data);
                _position += originalSize;
                checksum = encoded.Checksum;
                break;
            }
            case NarStoreType.EncodedAndCompressed:
            {
                // 存储形式是 XOR(LZ(明文))：先压缩，压缩结果再按路径密钥异或后落盘。
                // 校验和覆盖最终写出的存储字节，与客户端的 Verify 行为一致。
                originalSize = content.Length;
                var obfuscated = new XorWriteStream(_data, EntryKey.Create(pathBytes));
                _position += NarLzCodec.Compress(content, obfuscated);
                obfuscated.Flush();
                checksum = obfuscated.Checksum;
                break;
            }
            default:
                throw new NotSupportedException($"不支持的存储类型 {(int)storeType}。");
        }

        long storeSize = _position - offset;
        if (storeSize > int.MaxValue)
            throw new InvalidOperationException($"条目过大（{storeSize} 字节），NAR 目录表无法记录：{path}");
        if (originalSize > int.MaxValue)
            throw new InvalidOperationException($"条目过大（{originalSize} 字节），NAR 目录表无法记录：{path}");

        var entry = new NarEntry
        {
            Path = path,
            StoreType = storeType,
            Offset = offset,
            StoreSize = storeSize,
            Size = originalSize,
            LastModified = lastModified,
            Checksum = checksum,
        };
        _entries.Add(entry);
        return entry;
    }

    private void WriteData(byte[] buffer) => _data.Write(buffer, 0, buffer.Length);

    private static long CopyThrough(Stream source, Stream destination)
    {
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            total += read;
        }
        return total;
    }

    /// <summary>
    /// 打包目录表，压缩后追加到数据末尾，再把临时文件移到目标位置。
    /// <paramref name="tableOrder"/> 用于重排目录表中的条目：数据区按写入顺序落盘，
    /// 目录表却可以按另一顺序排列，这样才能复现原归档的布局。
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
