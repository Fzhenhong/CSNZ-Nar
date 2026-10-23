namespace NarTool.Core;

/// <summary>
/// 可增长的内存缓冲。替代 MemoryStream + ToArray 的组合，
/// 避免 ToArray 再复制一整份数据（大条目可省下数百 MB 的瞬时内存与拷贝开销）。
/// </summary>
internal sealed class GrowBuffer
{
    private byte[] _data = Array.Empty<byte>();
    private int _length;

    public int Length => _length;
    public ReadOnlySpan<byte> Span => _data.AsSpan(0, _length);

    /// <summary>空出至少 capacity 字节的空间，返回可直接写入的跨度。</summary>
    public Span<byte> Reserve(int capacity)
    {
        Ensure(_length + capacity);
        return _data.AsSpan(_length, capacity);
    }

    /// <summary>追加一段数据。</summary>
    public void Append(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0) return;
        Ensure(_length + source.Length);
        source.CopyTo(_data.AsSpan(_length));
        _length += source.Length;
    }

    /// <summary>确认已追加 count 字节（配合 <see cref="Reserve"/> 使用）。</summary>
    public void Advance(int count) => _length += count;

    /// <summary>交出底层数组。长度即实际数据长度，调用方不得再写入。</summary>
    public ReadOnlyMemory<byte> ToMemory() => _data.AsMemory(0, _length);

    private void Ensure(int required)
    {
        if (required <= _data.Length) return;

        int size = _data.Length == 0 ? 64 * 1024 : _data.Length;
        while (size < required) size = size > int.MaxValue / 2 ? required : size * 2;
        Array.Resize(ref _data, size);
    }
}

/// <summary>
/// 可增长缓冲的只写流包装，供 LZ 压缩器作为输出目标。
/// 只负责写入；压缩完成后由调用方直接取 Span 做后续处理，不再复制。
/// </summary>
internal sealed class GrowBufferStream : Stream
{
    public GrowBuffer Buffer { get; } = new();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Buffer.Length;
    public override long Position { get => Buffer.Length; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => Buffer.Append(buffer);

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
