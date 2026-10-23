using System.Text;

namespace NarTool.Core;

/// <summary>模型文件（.mdl）的加密状态。</summary>
public enum ModelState
{
    /// <summary>不是 StudioMDL 文件，未做任何改动。</summary>
    NotModel,
    /// <summary>明文模型（版本号已为 9），无需处理。</summary>
    Plain,
    /// <summary>已解密。</summary>
    Decrypted,
    /// <summary>已加密。</summary>
    Encrypted,
    /// <summary>结构不符合预期，跳过。</summary>
    Invalid,
}

/// <summary>
/// StudioMDL 模型文件的 ICE 加解密。
///
/// 加密版本把逐骨骼的顶点坐标块与逐顶点的骨骼权重块用 ICE-256 加密，
/// 并把文件版本号从 9 改写为 20/21 作为标记。这里按同一套结构处理，
/// 只还原被加密的那部分数据，其余字节保持原样。
/// </summary>
public static class GameFileCrypt
{
    private const int StudioHeaderMagic = 1414743113; // "IDST"

    private static readonly byte[] Version20Key =
    {
        50, 166, 33, 224, 171, 107, 244, 44, 147, 198, 241, 150, 251, 56, 117, 104,
        186, 112, 19, 134, 224, 179, 113, 244, 227, 155, 7, 34, 12, 254, 136, 58,
    };

    private static readonly byte[] Version21Key =
    {
        34, 122, 25, 111, 123, 134, 125, 224, 140, 198, 241, 150, 251, 56, 117, 104,
        136, 122, 120, 134, 120, 134, 103, 112, 217, 145, 7, 58, 20, 116, 254, 34,
    };

    /// <summary>按文件扩展名判断是否需要处理。</summary>
    public static bool IsModelPath(string path) =>
        string.Equals(Path.GetExtension(path), ".mdl", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取模型的版本号；不是 StudioMDL 文件时返回 null。</summary>
    public static int? ReadModelVersion(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            return ReadModelVersion(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读取流的版本号；不是 StudioMDL 文件时返回 null。不改变流位置。</summary>
    public static int? ReadModelVersion(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) return null;
        if (stream.Length < 8) return null;

        long saved = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> head = stackalloc byte[8];
            int read = stream.Read(head);
            if (read < 8) return null;
            if (BitConverter.ToInt32(head) != StudioHeaderMagic) return null;
            return BitConverter.ToInt32(head[4..]);
        }
        finally
        {
            stream.Position = saved;
        }
    }

    /// <summary>
    /// 就地转换模型文件，并尽量保留原版本号语义：
    /// 解密时把原有的 20/21 记录下来，由调用方写入清单；
    /// 重新加密时传入该值即可还原成 21，而不是统一退回 20。
    /// </summary>
    public static ModelState TransformFile(string path, bool encrypt, int? originalVersion = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            1 << 16, FileOptions.RandomAccess);
        return Transform(stream, encrypt, originalVersion);
    }

    /// <summary>
    /// 就地转换模型文件。与旧工具不同，这里以独占读写方式打开，
    /// 转换完成后写回同一文件，不产生备份。
    /// </summary>
    public static ModelState TransformFile(string path, bool encrypt)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            1 << 16, FileOptions.RandomAccess);
        return Transform(stream, encrypt);
    }

    public static ModelState Transform(Stream stream, bool encrypt, int? originalVersion = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("流必须可读、可写且可定位。", nameof(stream));

        long length = stream.Length;
        if (length < 244 || length > int.MaxValue) return ModelState.Invalid;

        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        stream.Position = 0;
        if (reader.ReadInt32() != StudioHeaderMagic) return ModelState.NotModel;

        int version = reader.ReadInt32();

        // 版本号 9 表示明文，20/21 表示对应密钥的加密版本，同时充当状态标记
        if (encrypt && version != 9) return ModelState.Plain;
        if (!encrypt && version != 20 && version != 21) return ModelState.Plain;

        // 加密时优先用调用方记录的原始版本，这样 21 号密钥能原样还原
        int encryptedVersion = originalVersion is 20 or 21 ? originalVersion.Value : 20;

        Ice ice = new(4);
        ice.SetKey(version == 21 || (encrypt && encryptedVersion == 21) ? Version21Key : Version20Key);

        stream.Position = 4;
        writer.Write(encrypt ? encryptedVersion : 9);
        writer.Flush();

        try
        {
            TransformBodyGroups(stream, reader, writer, ice, encrypt);
            TransformBodyParts(stream, reader, writer, ice, encrypt);
        }
        finally
        {
            writer.Flush();
        }

        return encrypt ? ModelState.Encrypted : ModelState.Decrypted;
    }

    /// <summary>bodypart：每个骨骼的顶点坐标块（顶点数 * 坐标数 + 768 字节固定尾巴）。</summary>
    private static void TransformBodyGroups(Stream s, BinaryReader r, BinaryWriter w, Ice ice, bool encrypt)
    {
        s.Position = 180;
        int count = r.ReadInt32();
        int offset = r.ReadInt32();

        for (int i = 0; i < count && offset >= 0; i++, offset += 80)
        {
            s.Position = offset + 68;
            int vertexCount = r.ReadInt32();
            int unused = r.ReadInt32();
            int dataOffset = r.ReadInt32();
            if (vertexCount < 0 || unused < 0 || dataOffset < 0) continue;

            int size = vertexCount * unused + 768;
            if (dataOffset + size > s.Length) continue;

            TransformRegion(s, r, w, ice, dataOffset, size, encrypt);
        }
    }

    /// <summary>bodypart 内的网格：每个网格的骨骼权重块（顶点数 * 12 字节）。</summary>
    private static void TransformBodyParts(Stream s, BinaryReader r, BinaryWriter w, Ice ice, bool encrypt)
    {
        s.Position = 204;
        int bodyPartCount = r.ReadInt32();
        int bodyPartOffset = r.ReadInt32();

        for (int i = 0; i < bodyPartCount && bodyPartOffset >= 0; i++, bodyPartOffset += 76)
        {
            s.Position = bodyPartOffset + 64;
            int meshCount = r.ReadInt32();
            s.Position += 4;
            int meshOffset = r.ReadInt32();
            if (meshOffset < 0) continue;

            for (int m = 0; m < meshCount && meshOffset >= 0; m++, meshOffset += 112)
            {
                s.Position = meshOffset + 80;
                int vertexCount = r.ReadInt32();
                s.Position += 4;
                int weightOffset = r.ReadInt32();
                if (weightOffset < 0) continue;

                int size = vertexCount * 12;
                if (weightOffset + size > s.Length) continue;

                TransformRegion(s, r, w, ice, weightOffset, size, encrypt);
            }
        }
    }

    /// <summary>
    /// ICE 按 8 字节块工作，因此只转换区间内完整的块，末尾不足一块的字节保持原样
    /// （与原工具行为一致，也不会越界改写相邻数据）。
    /// </summary>
    private static void TransformRegion(Stream s, BinaryReader r, BinaryWriter w, Ice ice, long offset, int size, bool encrypt)
    {
        int aligned = size & ~7;
        if (aligned <= 0 || offset + aligned > s.Length) return;

        s.Position = offset;
        byte[] buffer = r.ReadBytes(aligned);
        if (buffer.Length != aligned) return;

        if (encrypt) ice.EncryptBlocks(buffer, 0, aligned);
        else ice.DecryptBlocks(buffer, 0, aligned);

        s.Position = offset;
        w.Write(buffer, 0, aligned);
        w.Flush();
    }
}
