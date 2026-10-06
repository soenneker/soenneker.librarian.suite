using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

public sealed partial class LibrarianContainer
{
    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        using (await _mutationGate.Lock(cancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_items.TryGetValue(id, out string? document)) return null;
            if (!_versions.TryGetValue(id, out string? version))
                _versions[id] = version = Guid.NewGuid().ToString();
            return new LibrarianItem<string>(document, version);
        }
    }

    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        string next;
        using (await _mutationGate.Lock(cancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_items.ContainsKey(id) || !_versions.TryGetValue(id, out string? current) || current != version) return null;
            IndexKey?[] keys = PrepareIndexKeys(document);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _items[id] = document;
                _versions[id] = next = Guid.NewGuid().ToString();
                _queryScanSnapshot = null;
                ApplyIndexKeys(id, keys);
                UpdateAutomaticIndexes(id, document);
            }
            finally { Array.Clear(keys); }
        }
        await _database.MarkDirty(_containerName, CancellationToken.None);
        return new LibrarianItem<string>(document, next);
    }

    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        using (await _mutationGate.Lock(cancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_items.ContainsKey(id) || !_versions.TryGetValue(id, out string? current) || current != version) return false;
            _items.Remove(id);
            _versions.Remove(id);
            _queryScanSnapshot = null;
            foreach (DocumentIndex index in _indexes.Values) index.Remove(id);
            foreach (AutomaticIndex index in _automaticIndexes.Values) index.Index.Remove(id);
        }
        await _database.MarkDirty(_containerName, CancellationToken.None);
        return true;
    }
}
