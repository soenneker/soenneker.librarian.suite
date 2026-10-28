using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;

namespace Soenneker.Librarian.Cosmos;

internal sealed class CosmosUtf8Stream : MemoryStream
{
    private byte[]? _rented;
    private readonly int _count;

    public CosmosUtf8Stream(string json) : this(json, Encoding.UTF8.GetByteCount(json)) { }
    private CosmosUtf8Stream(string json, int count) : this(json, count, ArrayPool<byte>.Shared.Rent(count)) { }
    private CosmosUtf8Stream(string json, int count, byte[] buffer) : base(buffer, 0, count, writable: false)
    {
        _rented = buffer;
        _count = count;
        Encoding.UTF8.GetBytes(json.AsSpan(), buffer);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        byte[]? buffer = Interlocked.Exchange(ref _rented, null);
        if (buffer is null) return;
        buffer.AsSpan(0, _count).Clear();
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
