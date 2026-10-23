namespace NarTool.Core;

/// <summary>条目在归档中的存储方式。数值直接写入 NAR 目录表，不可更改。</summary>
public enum NarStoreType
{
    /// <summary>原样存储，不编码、不压缩。</summary>
    Raw = 0,

    /// <summary>按路径派生的密钥做异或编码。</summary>
    Encoded = 1,

    /// <summary>先异或编码再 LZ 压缩（官方 NAR 常用）。</summary>
    EncodedAndCompressed = 2,
}

/// <summary>NAR 容器与目录表的全部常量，取值来自对原工具（CSOToolLib）的逆向。</summary>
public static class NarFormat
{
    /// <summary>文件头与尾部签名，即 ASCII "NAR\0" 的小端整数。</summary>
    public const int Signature = 5390670;

    /// <summary>容器版本，固定 0x01000000。</summary>
    public const int ContainerVersion = 16777216;

    /// <summary>目录表版本，固定 1。</summary>
    public const int HeaderVersion = 1;

    /// <summary>尾部长度字段的混淆常量：尾部第一个 int32 为"目录表压缩长度 XOR 该值"。</summary>
    public const int LengthXorKey = 0x4076551F;

    /// <summary>尾部大小：长度字段 + 签名，共 8 字节。</summary>
    public const int FooterSize = 8;

    /// <summary>数据区起始偏移（跳过 8 字节文件头）。</summary>
    public const int DataStart = 8;

    /// <summary>目录表固定前缀长度：版本 + 3 个保留字段 + 条目数。</summary>
    public const int HeaderPreambleSize = 20;

    /// <summary>目录表末尾每个条目的固定字段长度（不含路径）。</summary>
    public const int EntryFixedSize = 24;

    /// <summary>目录表 XOR 混淆表（16 字节循环）。</summary>
    public static readonly byte[] TableXor = { 25, 91, 123, 44, 101, 94, 121, 37, 110, 75, 7, 33, 98, 127, 0, 41 };

    /// <summary>LZ 压缩的滑动字典大小，同时限制最大回溯距离。</summary>
    public const int DictionarySize = 8192;

    /// <summary>目录表解压后的安全上限，防止损坏文件触发超大内存分配。</summary>
    public const int MaxHeaderSize = 256 * 1024 * 1024;

    /// <summary>新打包时写入的 3 个保留字段，与原打包器保持一致以保证客户端可读。</summary>
    public static readonly int[] PreambleFields = { 0, 4, 1 };

    public static string TypeName(NarStoreType type) => type switch
    {
        NarStoreType.Raw => "原始",
        NarStoreType.Encoded => "编码",
        NarStoreType.EncodedAndCompressed => "编码+压缩",
        _ => "未知(" + (int)type + ")",
    };
}
