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

    internal List<IdValuePair> SnapshotForBatch(Dictionary<string, LibrarianPreparedWrite>? state = null)
    {
        ThrowIfDisposed();
        var result = new List<IdValuePair>(_items.Count);
        foreach (KeyValuePair<string, string> pair in _items)
        {
            string? value = state is not null && state.TryGetValue(pair.Key, out LibrarianPreparedWrite write) ? write.Value : pair.Value;
            if (value is not null) result.Add(new IdValuePair { Id = pair.Key, Value = value });
        }
        if (state is not null)
            foreach ((string id, LibrarianPreparedWrite write) in state)
                if (write.Value is not null && !_items.ContainsKey(id)) result.Add(new IdValuePair { Id = id, Value = write.Value });
        return result;
    }

    internal void PrepareBatchWrite(LibrarianWrite write, Dictionary<string, LibrarianPreparedWrite> state, CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        if (write.Value is null && !_items.ContainsKey(write.Id)) return;
        if (string.Equals(_items.GetValueOrDefault(write.Id), write.Value, StringComparison.Ordinal))
        {
            // Stage the version change until the batch commits, without rebuilding indexes.
            state.Add(write.Id, new LibrarianPreparedWrite(write.Value, [], []));
            return;
        }
        IndexKey?[] keys = [];
        IndexKey?[] automaticKeys = [];
        if (write.Value is not null)
        {
            if (_indexes.Count > 0)
            {
                keys = new IndexKey?[_indexes.Count];
                using JsonDocument document = JsonDocument.Parse(write.Value);
                var i = 0;
                foreach (DocumentIndex index in _indexes.Values) keys[i++] = index.Extract(document.RootElement);
            }
            if (_automaticIndexes.Count > 0)
            {
                automaticKeys = new IndexKey?[_automaticIndexes.Count];
                var i = 0;
                foreach (AutomaticIndexGroup group in _automaticIndexGroups.Values)
                {
                    object? value = group.Deserialize(write.Value);
                    foreach (AutomaticIndex index in group.Indexes) automaticKeys[i++] = index.Extract(value);
                }
            }
        }
        state.Add(write.Id, new LibrarianPreparedWrite(write.Value, keys, automaticKeys));
    }

    internal void PublishBatch(Dictionary<string, LibrarianPreparedWrite> state)
    {
        if (state.Count == 0) return;
        foreach ((string id, LibrarianPreparedWrite write) in state)
        {
            if (write.Value is null)
            {
                _items.Remove(id);
                _versions.Remove(id);
                foreach (DocumentIndex index in _indexes.Values) index.Remove(id);
                foreach (AutomaticIndex index in _automaticIndexes.Values) index.Index.Remove(id);
            }
            else
            {
                _versions[id] = Guid.NewGuid().ToString();
                if (string.Equals(_items.GetValueOrDefault(id), write.Value, StringComparison.Ordinal)) continue;
                _items[id] = write.Value;
                ApplyIndexKeys(id, write.Keys);
                var i = 0;
                foreach (AutomaticIndexGroup group in _automaticIndexGroups.Values)
                    foreach (AutomaticIndex index in group.Indexes) index.Index.Set(id, write.AutomaticKeys[i++]);
            }
        }
        _queryScanSnapshot = null;
    }
}
