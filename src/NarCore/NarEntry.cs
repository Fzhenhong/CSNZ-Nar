namespace NarTool.Core;

/// <summary>目录表中的一个条目。字段与 NAR 目录表结构一一对应。</summary>
public sealed class NarEntry
{
    /// <summary>归档内路径，形如 "/cstrike/foo.wad"（始终以 '/' 开头，UTF-8 存储）。</summary>
    public required string Path { get; init; }

    public required NarStoreType StoreType { get; init; }

    /// <summary>数据区内的起始偏移（相对文件开头，已包含 8 字节头部）。</summary>
    public required long Offset { get; init; }

    /// <summary>归档中实际占用的字节数（编码/压缩之后）。</summary>
    public required long StoreSize { get; init; }

    /// <summary>解包后的原始大小。</summary>
    public required long Size { get; init; }

    public required DateTime LastModified { get; init; }

    /// <summary>对"已编码但未压缩"的存储字节计算的 CRC32。</summary>
    public required uint Checksum { get; init; }

    /// <summary>去掉前导 '/' 的相对路径，用于拼接本地文件路径。</summary>
    public string RelativePath
    {
        get
        {
            string p = Path;
            if (p.StartsWith('/')) p = p.Substring(1);
            return p;
        }
    }

    public override string ToString() => Path;
}
