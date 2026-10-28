using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace Soenneker.Librarian.Cosmos;

// Operation-owned storage: never shared across concurrent requests or retained by a serializer.
internal sealed class CosmosJsonBuffer : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    public CosmosJsonBuffer(int capacity = 256) => _buffer = ArrayPool<byte>.Shared.Rent(capacity);
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _written)) throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0) { Grow(sizeHint); return _buffer.AsMemory(_written); }
    public Span<byte> GetSpan(int sizeHint = 0) { Grow(sizeHint); return _buffer.AsSpan(_written); }
    private void Grow(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        sizeHint = Math.Max(1, sizeHint);
        if (sizeHint <= _buffer.Length - _written) return;
        byte[] next = ArrayPool<byte>.Shared.Rent(Math.Max(checked(_written + sizeHint), checked(_buffer.Length * 2)));
        WrittenSpan.CopyTo(next);
        Return();
        _buffer = next;
    }
    public void Write(string text)
    {
        int count = Encoding.UTF8.GetByteCount(text);
        Advance(Encoding.UTF8.GetBytes(text.AsSpan(), GetSpan(count)));
    }
    public MemoryStream OpenRead() => new(_buffer, 0, _written, writable: false);
    private void Return()
    {
        _buffer.AsSpan(0, _written).Clear();
        ArrayPool<byte>.Shared.Return(_buffer);
    }
    public void Dispose()
    {
        if (_buffer.Length == 0) return;
        Return();
        _buffer = [];
        _written = 0;
    }
}
