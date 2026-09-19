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
        var result = new List<IdValuePair>(_items.Count);
        foreach (KeyValuePair<string, string> pair in _items)
        {
            string? value = state is not null && state.Writes.TryGetValue(pair.Key, out LibrarianPreparedWrite? write) ? write.Value : pair.Value;
            if (value is not null) result.Add(new IdValuePair { Id = pair.Key, Value = value });
        }
        if (state is not null)
            foreach ((string id, LibrarianPreparedWrite write) in state.Writes)
                if (write.Value is not null && !_items.ContainsKey(id)) result.Add(new IdValuePair { Id = id, Value = write.Value });
        return result;
    }

    internal LibrarianContainerState PrepareBatch(IEnumerable<LibrarianWrite> writes, CancellationToken token)
    {
        ThrowIfDisposed();
        var prepared = new Dictionary<string, LibrarianPreparedWrite>(StringComparer.OrdinalIgnoreCase);
        foreach (LibrarianWrite write in writes)
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(_items.GetValueOrDefault(write.Id), write.Value, StringComparison.Ordinal)) continue;
            IndexKey?[] keys = [];
            IndexKey?[] automaticKeys = [];
            if (write.Value is not null)
            {
                if (_indexes.Count > 0)
                {
                    keys = new IndexKey?[_indexes.Count];
                    using JsonDocument document = JsonDocument.Parse(write.Value);
                    int i = 0;
                    foreach (DocumentIndex index in _indexes.Values) keys[i++] = index.Extract(document.RootElement);
                }
                if (_automaticIndexes.Count > 0)
                {
                    automaticKeys = new IndexKey?[_automaticIndexes.Count];
                    int i = 0;
                    foreach (AutomaticIndexGroup group in _automaticIndexGroups.Values)
                    {
                        object? value = group.Deserialize(write.Value);
                        foreach (AutomaticIndex index in group.Indexes) automaticKeys[i++] = index.Extract(value);
                    }
                }
            }
            prepared.Add(write.Id, new LibrarianPreparedWrite(write.Value, keys, automaticKeys));
        }
        return new(prepared);
    }

    internal void PublishBatch(LibrarianContainerState state)
    {
        if (state.Writes.Count == 0) return;
        foreach ((string id, LibrarianPreparedWrite write) in state.Writes)
        {
            if (write.Value is null)
            {
                _items.Remove(id);
                foreach (DocumentIndex index in _indexes.Values) index.Remove(id);
                foreach (AutomaticIndex index in _automaticIndexes.Values) index.Index.Remove(id);
            }
            else
            {
                _items[id] = write.Value;
                ApplyIndexKeys(id, write.Keys);
                int i = 0;
                foreach (AutomaticIndexGroup group in _automaticIndexGroups.Values)
                    foreach (AutomaticIndex index in group.Indexes) index.Index.Set(id, write.AutomaticKeys[i++]);
            }
        }
        _queryScanSnapshot = null;
    }
}
