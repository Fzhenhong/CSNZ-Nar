namespace NarTool.Core;

/// <summary>
/// 标准 CRC-32（IEEE 802.3，多项式 0xEDB88320，初值与终值取反），
/// 与 SharpZipLib Crc32 及 NAR 目录表中的校验和一致。
/// 自实现以支持 Span，避免额外内存拷贝。
/// </summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private uint _crc = 0xFFFFFFFFu;

    public uint Value
    {
        get => _crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public void Reset() => _crc = 0xFFFFFFFFu;

    public void Update(ReadOnlySpan<byte> data)
    {
        uint crc = _crc;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Update(data);
        return crc.Value;
    }

    /// <summary>把整个流读空并返回其 CRC32，同时返回读取的字节数。</summary>
    public static (uint Crc, long Length) Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var crc = new Crc32();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            crc.Update(buffer.AsSpan(0, read));
            total += read;
        }
        return (crc.Value, total);
    }
}
