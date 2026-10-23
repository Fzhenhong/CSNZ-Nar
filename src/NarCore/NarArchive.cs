using System.Collections.ObjectModel;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace NarTool.Core;

/// <summary>
/// NAR 归档读取器。
///
/// 文件结构：
///   [0..8)                 签名 "NAR\0" + 版本 0x01000000
///   [8 .. 数据区末)         各条目的存储数据（偏移与长度由目录表给出）
///   目录表（BZip2 压缩后再按 16 字节表异或）
///   尾部 8 字节：压缩长度 XOR 0x4076551F + 签名
///
/// 目录表解压后为：
///   版本(1) + 3 个保留字段 + 条目数(count) + count 个条目：
///     uint16 路径长度 + UTF-8 路径 + int32 存储类型 + uint32 偏移 +
///     int32 存储大小 + int32 原始大小 + int32 Unix 时间 + uint32 CRC32
/// </summary>
public sealed class NarArchive : IDisposable
{
    private readonly List<NarEntry> _entries = new();

    private NarArchive(string path, NarDataFile data)
    {
        FilePath = path;
        Data = data;
    }

    public string FilePath { get; }

    public NarDataFile Data { get; }

    public ReadOnlyCollection<NarEntry> Entries => _entries.AsReadOnly();

    /// <summary>目录表在文件中占用的字节数（压缩后，含尾部）。</summary>
    public long TableSize { get; private set; }

    public static NarArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var data = new NarDataFile(path);
        try
        {
            var archive = new NarArchive(path, data);
            archive.ReadTable();
            return archive;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private void ReadTable()
    {
        long length = Data.Length;
        if (length < NarFormat.DataStart + NarFormat.FooterSize)
            throw new InvalidDataException("文件过小，不是有效的 NAR 归档。");

        // 头部与尾部签名校验
        Span<byte> head = stackalloc byte[NarFormat.DataStart];
        Data.Read(0, head);
        if (BitConverter.ToInt32(head) != NarFormat.Signature)
            throw new InvalidDataException("NAR 签名不匹配，可能不是 NAR 文件（或已损坏）。");
        if (BitConverter.ToInt32(head[4..]) != NarFormat.ContainerVersion)
            throw new InvalidDataException("NAR 版本不受支持。");

        Span<byte> footer = stackalloc byte[NarFormat.FooterSize];
        Data.Read(length - NarFormat.FooterSize, footer);
        if (BitConverter.ToInt32(footer[4..]) != NarFormat.Signature)
            throw new InvalidDataException("NAR 尾部签名不匹配，文件可能被截断。");

        int compressedSize = BitConverter.ToInt32(footer) ^ NarFormat.LengthXorKey;
        if (compressedSize < 0 || compressedSize > length - NarFormat.DataStart)
            throw new InvalidDataException($"NAR 目录表长度非法（{compressedSize}），文件可能已损坏。");

        TableSize = compressedSize + NarFormat.FooterSize;
        long tableStart = length - NarFormat.FooterSize - compressedSize;

        byte[] compressed = new byte[compressedSize];
        ReadExactly(tableStart, compressed);
        for (int i = 0; i < compressed.Length; i++)
            compressed[i] ^= NarFormat.TableXor[i & 0xF];

        byte[] table = DecompressTable(compressed);
        ParseEntries(table);
    }

    private static byte[] DecompressTable(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var output = new MemoryStream();
        using var bz = new BZip2InputStream(input) { IsStreamOwner = false };
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = bz.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > NarFormat.MaxHeaderSize)
                throw new InvalidDataException("NAR 目录表异常膨胀，文件可能已损坏。");
        }
        return output.ToArray();
    }

    private void ParseEntries(byte[] table)
    {
        if (table.Length < NarFormat.HeaderPreambleSize)
            throw new InvalidDataException("NAR 目录表过短。");
        if (BitConverter.ToInt32(table, 0) != NarFormat.HeaderVersion)
            throw new InvalidDataException("NAR 目录表版本不受支持。");

        int count = BitConverter.ToInt32(table, 16);
        if (count < 0 || (long)count * (NarFormat.EntryFixedSize + 2) > table.Length)
            throw new InvalidDataException($"NAR 目录表条目数非法（{count}）。");

        int offset = NarFormat.HeaderPreambleSize;
        for (int i = 0; i < count; i++)
        {
            if (offset + 2 > table.Length)
                throw new InvalidDataException($"NAR 目录表在第 {i} 项处被截断。");

            int pathLength = BitConverter.ToUInt16(table, offset);
            offset += 2;
            if (offset + pathLength + NarFormat.EntryFixedSize > table.Length)
                throw new InvalidDataException($"NAR 目录表在第 {i} 项处被截断。");

            string entryPath = Encoding.UTF8.GetString(table, offset, pathLength);
            offset += pathLength;

            var storeType = (NarStoreType)BitConverter.ToInt32(table, offset);
            long dataOffset = BitConverter.ToUInt32(table, offset + 4);
            long storeSize = BitConverter.ToInt32(table, offset + 8);
            long size = BitConverter.ToInt32(table, offset + 12);
            long unixTime = BitConverter.ToInt32(table, offset + 16);
            uint checksum = BitConverter.ToUInt32(table, offset + 20);
            offset += NarFormat.EntryFixedSize;

            _entries.Add(new NarEntry
            {
                Path = entryPath,
                StoreType = storeType,
                Offset = dataOffset,
                StoreSize = storeSize,
                Size = size,
                LastModified = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime,
                Checksum = checksum,
            });
        }
    }

    private void ReadExactly(long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = Data.Read(offset + total, buffer[total..]);
            if (read <= 0) throw new InvalidDataException("读取 NAR 文件时意外到达末尾。");
            total += read;
        }
    }

    /// <summary>
    /// 把条目解包并写入 <paramref name="destination"/>，返回写出的原始字节数。
    /// 全程流式处理，不把整个文件读入内存。
    /// </summary>
    public long Extract(NarEntry entry, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

        if (entry.Size == 0) return 0;

        if (entry.StoreType is not (NarStoreType.Raw or NarStoreType.Encoded or NarStoreType.EncodedAndCompressed))
            throw new NotSupportedException($"不支持的存储类型 {(int)entry.StoreType}（{entry.Path}）。");

        using Stream stored = new RegionStream(Data, entry.Offset, entry.StoreSize);
        Stream current = stored;

        if (entry.StoreType != NarStoreType.Raw)
        {
            var decoded = new XorKeyStream(current, EntryKey.Create(entry.Path));
            if (entry.StoreType == NarStoreType.EncodedAndCompressed)
            {
                current = new LzDecompressStream(decoded, entry.Size);
            }
            else
            {
                current = decoded;
            }
        }

        long written = CopyTo(current, destination);
        return written;
    }

    /// <summary>校验条目的 CRC32（对存储字节计算），与目录表中的值比对。</summary>
    public bool Verify(NarEntry entry)
    {
        using Stream stored = new RegionStream(Data, entry.Offset, entry.StoreSize);
        var (crc, _) = Crc32.Compute(stored);
        return crc == entry.Checksum;
    }

    private static long CopyTo(Stream source, Stream destination)
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

    public void Dispose() => Data.Dispose();
}
