namespace NarTool.Core;

/// <summary>
/// 标准 CRC-32（IEEE 802.3，多项式 0xEDB88320，初值与终值取反），
/// 与 SharpZipLib Crc32 及 NAR 目录表中的校验和一致。
///
/// 采用切片表（slicing-by-8）：每轮消费 8 字节，比逐字节查表快约 3 倍，
/// 输出与原实现逐位相同。
/// </summary>
public sealed class Crc32
{
    private const int Slices = 8;
    private static readonly uint[] Table = BuildTable();

    private uint _crc = 0xFFFFFFFFu;

    public uint Value => _crc ^ 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var table = new uint[Slices * 256];

        // 第一切片：标准逐字节表
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        // 后续切片：由前一切片递推，实现一次处理 8 字节
        for (int slice = 1; slice < Slices; slice++)
        {
            for (int i = 0; i < 256; i++)
            {
                uint prev = table[(slice - 1) * 256 + i];
                table[slice * 256 + i] = (prev >> 8) ^ table[prev & 0xFF];
            }
        }

        return table;
    }

    public void Reset() => _crc = 0xFFFFFFFFu;

    public void Update(ReadOnlySpan<byte> data)
    {
        uint crc = _crc;
        var t = Table;
        int i = 0;

        // 一次 8 字节
        while (data.Length - i >= 8)
        {
            uint one = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24) ^ crc;
            crc = t[7 * 256 + (one & 0xFF)]
                ^ t[6 * 256 + ((one >> 8) & 0xFF)]
                ^ t[5 * 256 + ((one >> 16) & 0xFF)]
                ^ t[4 * 256 + (one >> 24)]
                ^ t[3 * 256 + data[i + 4]]
                ^ t[2 * 256 + data[i + 5]]
                ^ t[1 * 256 + data[i + 6]]
                ^ t[0 * 256 + data[i + 7]];
            i += 8;
        }

        while (i < data.Length)
            crc = t[0 * 256 + ((crc ^ data[i++]) & 0xFF)] ^ (crc >> 8);

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
