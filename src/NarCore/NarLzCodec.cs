namespace NarTool.Core;

/// <summary>
/// NAR 内置 LZ77 变体（8 KB 滑动字典）的流式编解码器。
///
/// 线段格式：首字节 b，n = b &gt;&gt; 5，n2 = b &amp; 0x1F
///   n == 0   字面量段，长度 n2 + 1（1..32），随后跟原始字节
///   n == 1..6 匹配段，长度 n + 2（3..8），随后跟 1 字节距离低 8 位，
///             距离 = ((n2 &lt;&lt; 8) | 低位) + 1
///   n == 7   匹配段，再读 1 字节 e，长度 = 9 + e（9..264），距离同上
///
/// 解码器保持跨读取的状态，可把任意长度的条目边解边写盘；
/// 编码器按 1 MB 输入块推进。两者的内存占用都与文件大小无关。
/// </summary>
public static class NarLzCodec
{
    public const int MinMatch = 3;
    public const int MaxMatch = 264;
    public const int MaxDistance = NarFormat.DictionarySize;
    public const int MaxLiteralRun = 32;

    /// <summary>一次性把压缩流解成字节数组，供小数据（如目录表）使用。</summary>
    public static byte[] Decompress(byte[] source, long expectedLength)
    {
        using var input = new MemoryStream(source, writable: false);
        using var output = expectedLength <= int.MaxValue
            ? new MemoryStream((int)expectedLength)
            : new MemoryStream();
        var decoder = new NarLzDecoder(input);
        decoder.DecodeTo(output, expectedLength);
        return output.ToArray();
    }

    // ================================================================ 编码

    private const int BlockSize = 1 << 20;
    private const int MaxChainLength = 128;
    private const int NiceMatchLength = 96;
    private const int HashBits = 16;
    private const int HashSize = 1 << HashBits;
    private const int WindowSize = MaxDistance + BlockSize + MaxMatch;

    /// <summary>把 source 压缩写入 destination，返回写出的字节数。</summary>
    public static long Compress(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        byte[] window = new byte[WindowSize];
        int[] chain = new int[WindowSize];
        int[] head = new int[HashSize];

        byte[] outBuf = new byte[64 * 1024];
        int outPos = 0;
        long written = 0;

        void FlushOut()
        {
            if (outPos == 0) return;
            destination.Write(outBuf, 0, outPos);
            written += outPos;
            outPos = 0;
        }

        void Put(int value)
        {
            outBuf[outPos++] = (byte)value;
            if (outPos == outBuf.Length) FlushOut();
        }

        int Fill(int offset)
        {
            int want = WindowSize - offset;
            int got = 0;
            while (got < want)
            {
                int r = source.Read(window, offset + got, want - got);
                if (r <= 0) break;
                got += r;
            }
            return got;
        }

        int history = 0;
        int loaded = Fill(0);

        while (loaded > 0)
        {
            int limit = history + loaded;

            Array.Fill(head, -1);
            for (int i = history; i < limit; i++) chain[i] = -1;

            // 用历史数据预热哈希链，使每块开头也能引用上一块末尾的内容
            for (int i = 0; i + MinMatch <= history; i++)
            {
                int h = Hash(window, i, HashBits);
                chain[i] = head[h];
                head[h] = i;
            }

            int literalStart = 0;
            int literalCount = 0;

            void FlushLiterals()
            {
                while (literalCount > 0)
                {
                    int run = Math.Min(literalCount, MaxLiteralRun);
                    Put(run - 1);
                    for (int i = 0; i < run; i++) Put(window[literalStart + i]);
                    literalStart += run;
                    literalCount -= run;
                }
            }

            int pos = history;
            while (pos < limit)
            {
                int maxLen = Math.Min(MaxMatch, limit - pos);
                int bestLen = 0;
                int bestDist = 0;

                if (maxLen >= MinMatch)
                {
                    int h = Hash(window, pos, HashBits);
                    int candidate = head[h];
                    int walk = MaxChainLength;
                    int minPos = pos - MaxDistance;
                    if (minPos < 0) minPos = 0;

                    while (candidate >= minPos && walk-- > 0)
                    {
                        if (window[candidate + bestLen] == window[pos + bestLen])
                        {
                            int len = 0;
                            while (len < maxLen && window[candidate + len] == window[pos + len]) len++;
                            if (len > bestLen)
                            {
                                bestLen = len;
                                bestDist = pos - candidate;
                                if (len >= NiceMatchLength || len == maxLen) break;
                            }
                        }
                        candidate = chain[candidate];
                    }

                    if (bestLen < MinMatch) { bestLen = 0; bestDist = 0; }
                }

                if (bestLen == 0)
                {
                    if (maxLen >= MinMatch)
                    {
                        int h = Hash(window, pos, HashBits);
                        chain[pos] = head[h];
                        head[h] = pos;
                    }
                    if (literalCount == 0) literalStart = pos;
                    literalCount++;
                    if (literalCount == MaxLiteralRun) FlushLiterals();
                    pos++;
                    continue;
                }

                FlushLiterals();

                int value = bestDist - 1;
                int n = bestLen - 2;
                if (n <= 6)
                {
                    Put((n << 5) | ((value >> 8) & 0x1F));
                }
                else
                {
                    Put(0xE0 | ((value >> 8) & 0x1F));
                    Put(bestLen - 9);
                }
                Put(value & 0xFF);

                int end = pos + bestLen;
                for (int i = pos; i < end; i++)
                {
                    if (i + MinMatch <= limit)
                    {
                        int hi = Hash(window, i, HashBits);
                        chain[i] = head[hi];
                        head[hi] = i;
                    }
                }
                pos = end;
            }

            FlushLiterals();

            int keep = Math.Min(MaxDistance, limit);
            Buffer.BlockCopy(window, limit - keep, window, 0, keep);
            history = keep;
            loaded = Fill(history);
        }

        FlushOut();
        return written;
    }

    internal static int Hash(byte[] data, int index, int bits)
    {
        uint v = (uint)(data[index] | (data[index + 1] << 8) | (data[index + 2] << 16));
        v *= 2654435761u;
        return (int)(v >> (32 - bits));
    }
}

/// <summary>NAR LZ 解码器。保留字典与半段状态，可分段解出整个条目。</summary>
public sealed class NarLzDecoder
{
    private const int Mask = NarFormat.DictionarySize - 1;

    private readonly Stream _source;
    private readonly byte[] _dict = new byte[NarFormat.DictionarySize];
    private readonly byte[] _input = new byte[64 * 1024];

    private int _dictWrite;
    private int _dictFill;
    private int _inPos;
    private int _inLen;

    // 当前段的剩余部分。字面量段与匹配段都可能在两次 DecodeTo 之间被截断，
    // 必须保留进度，否则下一段会被错误解析。
    private int _literalRemaining;
    private int _matchRemaining;
    private int _matchDistance;

    public NarLzDecoder(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>已输出的字节总数。</summary>
    public long Produced { get; private set; }

    private int NextByte()
    {
        if (_inPos >= _inLen)
        {
            _inLen = _source.Read(_input, 0, _input.Length);
            _inPos = 0;
            if (_inLen <= 0) throw new InvalidDataException("压缩数据在解压完成前意外结束。");
        }
        return _input[_inPos++];
    }

    /// <summary>向 destination 写出恰好 length 字节；数据不足时抛异常。</summary>
    public void DecodeTo(Stream destination, long length)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        byte[] outBuf = new byte[64 * 1024];
        int outPos = 0;
        long remaining = length;

        try
        {
            while (remaining > 0)
            {
                if (_literalRemaining > 0)
                {
                    int run = (int)Math.Min(_literalRemaining, Math.Min(remaining, outBuf.Length - outPos));
                    for (int i = 0; i < run; i++)
                    {
                        byte v = (byte)NextByte();
                        outBuf[outPos++] = v;
                        _dict[_dictWrite] = v;
                        _dictWrite = (_dictWrite + 1) & Mask;
                        if (_dictFill < NarFormat.DictionarySize) _dictFill++;
                    }
                    _literalRemaining -= run;
                    remaining -= run;
                    Produced += run;
                    if (outPos == outBuf.Length) { destination.Write(outBuf, 0, outPos); outPos = 0; }
                    continue;
                }

                if (_matchRemaining > 0)
                {
                    int run = (int)Math.Min(_matchRemaining, Math.Min(remaining, outBuf.Length - outPos));
                    for (int i = 0; i < run; i++)
                    {
                        byte v = _dict[(_dictWrite - _matchDistance) & Mask];
                        outBuf[outPos++] = v;
                        _dict[_dictWrite] = v;
                        _dictWrite = (_dictWrite + 1) & Mask;
                        if (_dictFill < NarFormat.DictionarySize) _dictFill++;
                    }
                    _matchRemaining -= run;
                    remaining -= run;
                    Produced += run;
                    if (outPos == outBuf.Length) { destination.Write(outBuf, 0, outPos); outPos = 0; }
                    continue;
                }

                int b = NextByte();
                int n = b >> 5;
                int n2 = b & 0x1F;

                if (n == 0)
                {
                    _literalRemaining = n2 + 1;
                }
                else
                {
                    if (n == 7) n += NextByte();
                    _matchRemaining = n + 2;
                    _matchDistance = ((n2 << 8) | NextByte()) + 1;
                    if (_matchDistance > _dictFill)
                        throw new InvalidDataException($"压缩数据引用了字典范围外的数据（距离 {_matchDistance}，已填充 {_dictFill}）。");
                }
            }
        }
        finally
        {
            if (outPos > 0) destination.Write(outBuf, 0, outPos);
        }
    }
}
