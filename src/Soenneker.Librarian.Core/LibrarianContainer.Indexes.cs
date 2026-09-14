using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Asyncs.Locks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Core.Indexes;
using Soenneker.Utils.Json;

namespace Soenneker.Librarian.Core;

public sealed partial class LibrarianContainer
{
    private readonly AsyncLock _mutationGate;
    private Dictionary<string, DocumentIndex> _indexes = new(StringComparer.Ordinal);
    private IndexKey?[] _preparedKeys = Array.Empty<IndexKey?>();

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPath);
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            if (_indexes.ContainsKey(fieldPath))
                return;
            var index = new DocumentIndex(fieldPath);
            foreach ((string id, string json) in _items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using JsonDocument document = JsonDocument.Parse(json);
                index.Set(id, index.Extract(document.RootElement));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var preparedKeys = new IndexKey?[_indexes.Count + 1];
            _indexes.Add(fieldPath, index);
            _preparedKeys = preparedKeys;
        }
    }

    public async ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(skip, take);
        ThrowIfDisposed();
        IndexKey key = IndexKey.FromValue(value);
        IndexPage page;
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            page = GetIndex(fieldPath).Equal(key, _items, skip, take, cancellationToken);
        }
        using (page)
            return Materialize<T>(fieldPath, page, cancellationToken);
    }

    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IndexKey key = IndexKey.FromValue(value);
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            return GetIndex(fieldPath).Count(key);
        }
    }

    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        return await CountByIndex(fieldPath, value, cancellationToken).NoSync() != 0;
    }

    public async ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        ValidatePage(skip, take);
        ThrowIfDisposed();
        IndexKey? lower = minimum is null ? null : IndexKey.FromValue(minimum);
        IndexKey? upper = maximum is null ? null : IndexKey.FromValue(maximum);
        IndexPage page;
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            page = GetIndex(fieldPath).Range(lower, upper, descending, _items, skip, take, cancellationToken);
        }
        using (page)
            return Materialize<T>(fieldPath, page, cancellationToken);
    }

    private DocumentIndex GetIndex(string fieldPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPath);
        return _indexes.TryGetValue(fieldPath, out DocumentIndex? index)
            ? index
            : throw new InvalidOperationException($"Index '{fieldPath}' does not exist. Call EnsureIndex before querying it.");
    }

    private static LibrarianQueryResult<T> Materialize<T>(string fieldPath, IndexPage page, CancellationToken cancellationToken)
    {
        return new LibrarianQueryResult<T>
        {
            Items = DeserializePage<T>(page, cancellationToken),
            Index = fieldPath,
            IndexEntriesExamined = page.Count,
            DocumentsDeserialized = page.Count
        };
    }

    private static T[] DeserializePage<T>(IndexPage page, CancellationToken cancellationToken)
    {
        T[] result = page.Count == 0 ? Array.Empty<T>() : new T[page.Count];
        for (var i = 0; i < result.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = JsonUtil.Deserialize<T>(page.Documents[i]);
            if (document is null)
                throw new InvalidDataException("An indexed document deserialized to null.");
            result[i] = document;
        }
        return result;
    }

    private static void ValidatePage(int skip, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
    }

    private IndexKey?[] PrepareIndexKeys(string json)
    {
        if (_indexes.Count == 0)
            return Array.Empty<IndexKey?>();
        using JsonDocument document = JsonDocument.Parse(json);
        IndexKey?[] keys = _preparedKeys;
        var i = 0;
        try
        {
            foreach (DocumentIndex index in _indexes.Values)
                keys[i++] = index.Extract(document.RootElement);
            return keys;
        }
        catch
        {
            Array.Clear(keys);
            throw;
        }
    }

    private void ApplyIndexKeys(string id, IndexKey?[] keys)
    {
        var i = 0;
        foreach (DocumentIndex index in _indexes.Values)
            index.Set(id, keys[i++]);
    }
}
