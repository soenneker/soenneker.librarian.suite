using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

public sealed partial class LibrarianContainer
{
    internal string? ReadForBatch(string id)
    {
        ThrowIfDisposed();
        return _items.GetValueOrDefault(id);
    }

    internal List<IdValuePair> SnapshotForBatch(LibrarianContainerState? state = null)
    {
        ThrowIfDisposed();
        Dictionary<string, string> items = state?.Items ?? _items;
        var result = new List<IdValuePair>(items.Count);
        foreach (KeyValuePair<string, string> pair in items) result.Add(new IdValuePair { Id = pair.Key, Value = pair.Value });
        return result;
    }

    internal LibrarianContainerState PrepareBatch(IEnumerable<LibrarianWrite> writes, CancellationToken token)
    {
        ThrowIfDisposed();
        var items = new Dictionary<string, string>(_items, StringComparer.OrdinalIgnoreCase);
        foreach (LibrarianWrite write in writes)
        {
            token.ThrowIfCancellationRequested();
            if (write.Value is null) items.Remove(write.Id);
            else items[write.Id] = write.Value;
        }
        var indexes = new Dictionary<string, DocumentIndex>(StringComparer.Ordinal);
        foreach (string path in _indexes.Keys)
            indexes.Add(path, new DocumentIndex(path));
        if (indexes.Count > 0)
        {
            foreach (KeyValuePair<string, string> pair in items)
            {
                token.ThrowIfCancellationRequested();
                using JsonDocument document = JsonDocument.Parse(pair.Value);
                foreach (DocumentIndex index in indexes.Values)
                    index.Set(pair.Key, index.Extract(document.RootElement));
            }
        }
        return new(items, indexes, new IndexKey?[indexes.Count]);
    }

    internal void PublishBatch(LibrarianContainerState state)
    {
        _items = state.Items;
        _indexes = state.Indexes;
        _preparedKeys = state.PreparedKeys;
        _automaticIndexes.Clear();
        _automaticIndexGroups.Clear();
        _queryScanSnapshot = null;
    }
}
