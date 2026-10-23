using Microsoft.Win32.SafeHandles;

namespace NarTool.Core;

/// <summary>
/// 以随机访问句柄打开的 NAR 数据文件。用 RandomAccess 定位读取，
/// 因此可以被多个线程同时使用，无需加锁。
/// </summary>
public sealed class NarDataFile : IDisposable
{
    private readonly SafeFileHandle _handle;

    public string FilePath { get; }

    public long Length { get; }

    public NarDataFile(string path)
    {
        FilePath = path;
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Length = RandomAccess.GetLength(_handle);
    }

    /// <summary>读取指定区间，返回实际读到的字节数。</summary>
    public int Read(long offset, Span<byte> buffer) => RandomAccess.Read(_handle, buffer, offset);

    public void Dispose() => _handle.Dispose();
}

/// <summary>把数据文件的一段区间包装成只读流，不复制数据。</summary>
public sealed class RegionStream : Stream
{
    private readonly NarDataFile _file;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public RegionStream(NarDataFile file, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start + length > file.Length)
            throw new InvalidDataException($"条目区间 [{start}, {start + length}) 超出文件长度 {file.Length}。");
        _file = file;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 && value <= _length
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0 || buffer.Length == 0) return 0;
        if (buffer.Length > remaining) buffer = buffer[..(int)remaining];

        int read = _file.Read(_start + _position, buffer);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > _length) throw new ArgumentOutOfRangeException(nameof(offset));
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>16 字节循环异或的共享实现，供读流与写流复用。</summary>
internal static class KeyXor
{
    internal static readonly int VectorWidth = System.Numerics.Vector<byte>.Count;

    /// <summary>把 16 字节密钥铺到一个向量宽度上。</summary>
    internal static System.Numerics.Vector<byte> BuildVector(byte[] key)
    {
        var wide = new byte[VectorWidth];
        for (int i = 0; i < VectorWidth; i++) wide[i] = key[i & 15];
        return System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, System.Numerics.Vector<byte>>(wide.AsSpan())[0];
    }

    /// <summary>
    /// 就地异或。keyOffset 为 buffer[0] 对应的密钥相位（0..15）。
    /// 先标量推进到 16 字节对齐，再向量批量处理，最后标量收尾。
    /// </summary>
    internal static void Apply(Span<byte> buffer, byte[] key, int keyOffset, System.Numerics.Vector<byte> keyVector)
    {
        int len = buffer.Length;
        if (len == 0) return;

        int start = keyOffset & 15;
        int i = 0;

        // 1) 标量推进到 16 字节对齐，使后续可用固定向量
        if (start != 0)
        {
            int head = Math.Min(len, 16 - start);
            for (int k = 0; k < head; k++)
                buffer[k] ^= key[(start + k) & 15];
            i = head;
        }

        // 2) 向量主体：此时 (start + i) 已是 16 的倍数，向量宽度也是 16 的倍数，
        //    因此整段可直接套用同一份铺满密钥的向量
        if (VectorWidth % 16 == 0)
        {
            int bulk = (len - i) / VectorWidth * VectorWidth;
            if (bulk > 0)
            {
                var vecs = System.Runtime.InteropServices.MemoryMarshal
                    .Cast<byte, System.Numerics.Vector<byte>>(buffer.Slice(i, bulk));
                for (int v = 0; v < vecs.Length; v++)
                    vecs[v] = System.Numerics.Vector.Xor(vecs[v], keyVector);
                i += bulk;
            }
        }

        // 3) 标量收尾。密钥相位始终相对整段起点计算，
        //    不能用重置后的局部相位，否则分段读取时后续块会错位
        for (; i < len; i++)
            buffer[i] ^= key[(start + i) & 15];
    }
}

/// <summary>按条目密钥做 16 字节循环异或的只读流。编码与解码是同一操作。</summary>
public sealed class XorKeyStream : Stream
{
    private readonly Stream _base;
    private readonly byte[] _key;
    private readonly System.Numerics.Vector<byte> _keyVector;
    private long _position;

    public XorKeyStream(Stream baseStream, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 16) throw new ArgumentException("密钥必须为 16 字节。", nameof(key));
        _base = baseStream;
        _key = key;
        _keyVector = KeyXor.BuildVector(key);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _base.Length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = _base.Read(buffer);
        if (read <= 0) return read;

        KeyXor.Apply(buffer.Slice(0, read), _key, (int)(_position & 0xF), _keyVector);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _base.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// 写入时按 16 字节循环异或并累计 CRC32 的包装流。
/// NAR 的"编码+压缩"条目存储的是 XOR(LZ(明文))，
/// 因此压缩结果要先经过这一层再落盘。
/// </summary>
public sealed class XorWriteStream : Stream
{
    private readonly Stream _base;
    private readonly byte[] _key;
    private readonly System.Numerics.Vector<byte> _keyVector;
    private readonly Crc32 _crc = new();
    private byte[] _scratch = new byte[64 * 1024];
    private long _position;

    public XorWriteStream(Stream baseStream, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 16) throw new ArgumentException("密钥必须为 16 字节。", nameof(key));
        _base = baseStream;
        _key = key;
        _keyVector = KeyXor.BuildVector(key);
    }

    public uint Checksum => _crc.Value;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _position;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0) return;

        // 复用一块临时缓冲，避免每次 Write 都分配
        if (_scratch.Length < buffer.Length) _scratch = new byte[Math.Max(buffer.Length, _scratch.Length * 2)];
        var work = _scratch.AsSpan(0, buffer.Length);
        buffer.CopyTo(work);

        KeyXor.Apply(work, _key, (int)(_position & 0xF), _keyVector);

        _crc.Update(work);
        _base.Write(work);
        _position += work.Length;
    }

    public override void Flush() => _base.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>读取时顺带累计 CRC32 的包装流，用于校验"存储字节"的校验和。</summary>
public sealed class Crc32TeeStream : Stream
{
    private readonly Stream _base;
    private readonly Crc32 _crc = new();

    public Crc32TeeStream(Stream baseStream)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        _base = baseStream;
    }

    public uint Checksum => _crc.Value;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _base.Length;
    public override long Position { get => _base.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = _base.Read(buffer);
        if (read > 0) _crc.Update(buffer[..read]);
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _base.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// 把 LZ 压缩数据流式解出为只读流。内部保留解码器状态，
/// 每次读取只解出所需长度，可在任意大小的文件上工作。
/// </summary>
public sealed class LzDecompressStream : Stream
{
    private readonly NarLzDecoder _decoder;
    private readonly long _outputLength;
    private readonly byte[] _buffer;
    private int _bufferPos;
    private int _bufferLen;
    private long _produced;

    public LzDecompressStream(Stream source, long outputLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(outputLength);
        _decoder = new NarLzDecoder(source);
        _outputLength = outputLength;
        _buffer = new byte[256 * 1024];
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _outputLength;
    public override long Position { get => _produced; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        if (_bufferPos >= _bufferLen)
        {
            if (_produced >= _outputLength) return 0;
            Refill();
            if (_bufferLen == 0) return 0;
        }

        int n = Math.Min(buffer.Length, _bufferLen - _bufferPos);
        _buffer.AsSpan(_bufferPos, n).CopyTo(buffer);
        _bufferPos += n;
        return n;
    }

    private void Refill()
    {
        long remaining = _outputLength - _produced;
        int want = (int)Math.Min(_buffer.Length, remaining);
        using var sink = new BoundedWriteStream(_buffer, want);
        _decoder.DecodeTo(sink, want);
        _produced += sink.Written;
        _bufferLen = (int)sink.Written;
        _bufferPos = 0;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>只写入前 capacity 字节的只写内存目标。</summary>
    private sealed class BoundedWriteStream : Stream
    {
        private readonly byte[] _target;
        private readonly int _capacity;

        public long Written { get; private set; }

        public BoundedWriteStream(byte[] target, int capacity)
        {
            _target = target;
            _capacity = capacity;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int room = _capacity - (int)Written;
            if (buffer.Length > room) buffer = buffer[..room];
            if (buffer.Length <= 0) return;
            buffer.CopyTo(_target.AsSpan((int)Written));
            Written += buffer.Length;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
