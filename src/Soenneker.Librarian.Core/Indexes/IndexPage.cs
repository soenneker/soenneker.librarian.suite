using System;
using System.Buffers;

namespace Soenneker.Librarian.Core.Indexes;

internal readonly struct IndexPage(string[] documents, int count) : IDisposable
{
    internal static IndexPage Empty => new(Array.Empty<string>(), 0);
    internal string[] Documents { get; } = documents;
    internal int Count { get; } = count;

    public void Dispose()
    {
        if (Count == 0) return;
        Array.Clear(Documents, 0, Count);
        ArrayPool<string>.Shared.Return(Documents);
    }
}
