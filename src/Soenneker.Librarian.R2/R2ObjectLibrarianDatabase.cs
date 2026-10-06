using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.R2.Abstract;

namespace Soenneker.Librarian.R2;

/// <summary>A multi-owner R2 database storing one versioned object per document. Point operations perform no startup load.</summary>
/// <remarks>Writes persist immediately. Conditional deletes retain tombstones. Multi-document transactions, LINQ and secondary
/// indexes are unsupported; use the snapshot provider only when its single-owner and whole-database semantics are intended.</remarks>
public sealed class R2ObjectLibrarianDatabase(IR2LibrarianObjectStore store, string key = "librarian") : ILibrarianDatabase
{
    private readonly ConcurrentDictionary<string, R2ObjectLibrarianContainer> _containers = new(StringComparer.Ordinal);
    private readonly string _prefix = "librarian/" + Segment(key) + "/";
    private bool _disposed;

    internal static string Segment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(Encoding.UTF8.GetBytes(value));
    }

    public ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        return ValueTask.FromResult<ILibrarianContainer>(_containers.GetOrAdd(containerName,
            name => new R2ObjectLibrarianContainer(store, _prefix + Segment(name) + "/")));
    }

    public ValueTask Save(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default) => Save(cancellationToken);

    public ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_containers.TryRemove(containerName, out R2ObjectLibrarianContainer? container)) return ValueTask.FromResult(false);
        container.Dispose();
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (R2ObjectLibrarianContainer container in _containers.Values) container.Dispose();
        _containers.Clear();
        return ValueTask.CompletedTask;
    }
}
