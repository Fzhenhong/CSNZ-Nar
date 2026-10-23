namespace NarTool.Core;

/// <summary>
/// ICE 分组密码（Nexon 模型文件用 n = 4，即 64 位分组 / 64 轮 / 256 位密钥）。
/// 实现自 CSOToolLib 的 IceCryptoTransform，仅保留 ECB 无填充的块变换。
/// </summary>
public sealed class Ice
{
    private static readonly int[,] SMod =
    {
        { 333, 313, 505, 369 },
        { 379, 375, 319, 391 },
        { 361, 445, 451, 397 },
        { 397, 425, 395, 505 },
    };

    private static readonly int[,] SXor =
    {
        { 131, 133, 155, 205 },
        { 204, 167, 173, 65 },
        { 75, 46, 212, 51 },
        { 234, 203, 46, 4 },
    };

    private static readonly uint[] PBox =
    {
        1u, 128u, 1024u, 8192u, 524288u, 2097152u, 16777216u, 1073741824u,
        8u, 32u, 256u, 16384u, 65536u, 8388608u, 67108864u, 536870912u,
        4u, 16u, 512u, 32768u, 131072u, 4194304u, 134217728u, 268435456u,
        2u, 64u, 2048u, 4096u, 262144u, 1048576u, 33554432u, 2147483648u,
    };

    private static readonly int[] KeyRotation = { 0, 1, 2, 3, 2, 1, 3, 0, 1, 3, 2, 0, 3, 1, 0, 2 };

    private static readonly uint[,] SBox = BuildSBox();

    private readonly int _rounds;
    private readonly int _size;
    private readonly uint[,] _keySchedule;

    public Ice(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (n == 0) { _size = 1; _rounds = 8; }
        else { _size = n; _rounds = n << 4; }
        _keySchedule = new uint[_rounds, 3];
    }

    /// <summary>密钥长度：n = 0 时为 8 字节，否则 n * 8 字节。</summary>
    public int KeySize => _rounds == 8 ? 8 : _size << 3;

    public void SetKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
            throw new ArgumentException($"密钥长度应为 {KeySize} 字节。", nameof(key));

        ushort[] builder = new ushort[4];
        if (_rounds == 8)
        {
            for (int i = 0; i < 4; i++)
                builder[3 - i] = (ushort)((key[i << 1] << 8) | key[(i << 1) + 1]);
            BuildSchedule(builder, 0, 0);
            return;
        }

        for (int block = 0; block < _size; block++)
        {
            int baseIndex = block << 3;
            for (int k = 0; k < 4; k++)
                builder[3 - k] = (ushort)((key[baseIndex + (k << 1)] << 8) | key[baseIndex + (k << 1) + 1]);
            BuildSchedule(builder, baseIndex, 0);
            BuildSchedule(builder, _rounds - 8 - baseIndex, 8);
        }
    }

    private static uint GFMultiply(uint a, uint b, uint m)
    {
        uint result = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) result ^= a;
            a <<= 1;
            b >>= 1;
            if (a >= 256) a ^= m;
        }
        return result;
    }

    private static uint GFExp7(uint b, uint m)
    {
        if (b == 0) return 0;
        uint b2 = GFMultiply(b, b, m);
        uint b3 = GFMultiply(b, b2, m);
        uint b7 = GFMultiply(b3, b3, m);
        return GFMultiply(b, b7, m);
    }

    private static uint Perm32(uint x)
    {
        uint result = 0;
        int bit = 0;
        while (x != 0)
        {
            if ((x & 1) != 0) result |= PBox[bit];
            bit++;
            x >>= 1;
        }
        return result;
    }

    private static uint[,] BuildSBox()
    {
        var box = new uint[4, 1024];
        for (int i = 0; i < 1024; i++)
        {
            int value = (i >> 1) & 0xFF;
            int sub = (i & 1) | ((i & 0x200) >> 8);
            box[0, i] = Perm32(GFExp7((uint)(value ^ SXor[0, sub]), (uint)SMod[0, sub]) << 24);
            box[1, i] = Perm32(GFExp7((uint)(value ^ SXor[1, sub]), (uint)SMod[1, sub]) << 16);
            box[2, i] = Perm32(GFExp7((uint)(value ^ SXor[2, sub]), (uint)SMod[2, sub]) << 8);
            box[3, i] = Perm32(GFExp7((uint)(value ^ SXor[3, sub]), (uint)SMod[3, sub]));
        }
        return box;
    }

    private void BuildSchedule(ushort[] keyBuilder, int scheduleBase, int rotationOffset)
    {
        for (int i = 0; i < 8; i++)
        {
            int rotation = KeyRotation[rotationOffset + i];
            int row = scheduleBase + i;
            _keySchedule[row, 0] = 0;
            _keySchedule[row, 1] = 0;
            _keySchedule[row, 2] = 0;

            for (int j = 0; j < 15; j++)
            {
                for (int k = 0; k < 4; k++)
                {
                    ushort value = keyBuilder[(rotation + k) & 3];
                    ushort bit = (ushort)(value & 1);
                    _keySchedule[row, j % 3] = (_keySchedule[row, j % 3] << 1) | bit;
                    keyBuilder[(rotation + k) & 3] = (ushort)((value >> 1) | ((bit ^ 1) << 15));
                }
            }
        }
    }

    private uint Transform(uint value, int subKeyIndex)
    {
        uint high = ((value >> 16) & 0x3FF) | (((value >> 14) | (value << 18)) & 0xFFC00);
        uint low = (value & 0x3FF) | ((value << 2) & 0xFFC00);
        uint mixed = _keySchedule[subKeyIndex, 2] & (high ^ low);
        uint left = mixed ^ high ^ _keySchedule[subKeyIndex, 0];
        uint right = mixed ^ low ^ _keySchedule[subKeyIndex, 1];
        return SBox[0, (int)(left >> 10)] | SBox[1, (int)(left & 0x3FF)]
             | SBox[2, (int)(right >> 10)] | SBox[3, (int)(right & 0x3FF)];
    }

    /// <summary>原地解密 length 字节（必须是 8 的倍数）。</summary>
    public void DecryptBlocks(byte[] data, int offset, int length) => Run(data, offset, length, encrypt: false);

    /// <summary>原地加密 length 字节（必须是 8 的倍数）。</summary>
    public void EncryptBlocks(byte[] data, int offset, int length) => Run(data, offset, length, encrypt: true);

    private void Run(byte[] data, int offset, int length, bool encrypt)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0 || length < 0 || offset + length > data.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        if ((length & 7) != 0)
            throw new ArgumentException("ICE 只能按 8 字节块处理。", nameof(length));

        for (int i = 0; i < length; i += 8)
        {
            if (encrypt) BlockEncrypt(data, offset + i);
            else BlockDecrypt(data, offset + i);
        }
    }

    private void BlockEncrypt(byte[] data, int p)
    {
        uint left = (uint)((data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3]);
        uint right = (uint)((data[p + 4] << 24) | (data[p + 5] << 16) | (data[p + 6] << 8) | data[p + 7]);

        for (int round = 0; round < _rounds; round += 2)
        {
            left ^= Transform(right, round);
            right ^= Transform(left, round + 1);
        }

        Store(data, p, right, left);
    }

    private void BlockDecrypt(byte[] data, int p)
    {
        uint left = (uint)((data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3]);
        uint right = (uint)((data[p + 4] << 24) | (data[p + 5] << 16) | (data[p + 6] << 8) | data[p + 7]);

        for (int round = _rounds - 1; round > 0; round -= 2)
        {
            left ^= Transform(right, round);
            right ^= Transform(left, round - 1);
        }

        Store(data, p, right, left);
    }

    private static void Store(byte[] data, int p, uint first, uint second)
    {
        data[p] = (byte)(first >> 24);
        data[p + 1] = (byte)(first >> 16);
        data[p + 2] = (byte)(first >> 8);
        data[p + 3] = (byte)first;
        data[p + 4] = (byte)(second >> 24);
        data[p + 5] = (byte)(second >> 16);
        data[p + 6] = (byte)(second >> 8);
        data[p + 7] = (byte)second;
    }
}
