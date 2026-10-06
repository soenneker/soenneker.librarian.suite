using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.R2.Abstract;

namespace Soenneker.Librarian.R2;

public sealed class R2ObjectLibrarianContainer(IR2LibrarianObjectStore store, string prefix) : ILibrarianContainer
{
    private bool _disposed;
    private string Key(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return prefix + R2ObjectLibrarianDatabase.Segment(id) + ".json";
    }

    private static R2DocumentEnvelope Decode(R2LibrarianObject value) =>
        JsonSerializer.Deserialize(value.Content.Span, R2DocumentJsonContext.Default.R2DocumentEnvelope)
        ?? throw new InvalidDataException("The R2 document envelope is null.");

    private ValueTask<string?> Write(string id, string? document, string? expected, CancellationToken token) =>
        store.Write(Key(id), JsonSerializer.SerializeToUtf8Bytes(new R2DocumentEnvelope(id, Guid.NewGuid().ToString(), document),
            R2DocumentJsonContext.Default.R2DocumentEnvelope), expected, token);

    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        R2LibrarianObject? value = await store.Read(Key(id), cancellationToken: cancellationToken);
        if (value is null) return null;
        R2DocumentEnvelope envelope = Decode(value);
        if (envelope.Id != id || string.IsNullOrWhiteSpace(envelope.Revision)) throw new InvalidDataException("Invalid R2 document identity or revision.");
        return envelope.Document is null ? null : new LibrarianItem<string>(envelope.Document, value.ETag);
    }

    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        string? next = await Write(id, document, version, cancellationToken);
        return next is null ? null : new LibrarianItem<string>(document, next);
    }

    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return await Write(id, null, version, cancellationToken) is not null;
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        R2LibrarianObject? current = await store.Read(Key(id), cancellationToken: cancellationToken);
        if (current is not null && Decode(current).Document is not null ||
            await Write(id, document, current?.ETag, cancellationToken) is null)
            throw new InvalidOperationException($"Document '{id}' already exists or was concurrently created.");
        return document;
    }

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default) =>
        (await GetItemWithVersion(id, cancellationToken))?.Document;

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            LibrarianItem<string>? current = await GetItemWithVersion(id, cancellationToken);
            if (current is null) return null;
            if (await UpdateItemIfVersion(id, document, current.Version, cancellationToken) is not null) return document;
        }
        throw new LibrarianConcurrencyException($"Document '{id}' changed during all update attempts.");
    }

    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            LibrarianItem<string>? current = await GetItemWithVersion(id, cancellationToken);
            if (current is null) throw new KeyNotFoundException($"Document '{id}' does not exist.");
            if (await DeleteItemIfVersion(id, current.Version, cancellationToken)) return;
        }
        throw new LibrarianConcurrencyException($"Document '{id}' changed during all delete attempts.");
    }

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new List<IdValuePair>();
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            R2LibrarianObjectPage page = await store.List(prefix, cursor, cancellationToken);
            foreach (string key in page.Keys)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("R2 listing escaped its container prefix.");
                R2LibrarianObject? value = await store.Read(key, cancellationToken: cancellationToken);
                if (value is null) continue;
                R2DocumentEnvelope envelope = Decode(value);
                if (key != Key(envelope.Id)) throw new InvalidDataException("R2 document identity does not match its key.");
                if (envelope.Document is not null) result.Add(new IdValuePair { Id = envelope.Id, Value = envelope.Document });
            }
            cursor = page.Cursor;
            if (cursor is not null && !cursors.Add(cursor)) throw new InvalidDataException("R2 returned a repeated listing cursor.");
        } while (cursor is not null);
        return result;
    }

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) =>
        (await GetLibrarianItems(cancellationToken)).Select(item => item.Value).ToList();
    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default) =>
        (await GetLibrarianItems(cancellationToken)).Select(item => item.Id).ToList();
    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        foreach (string id in await GetAllIds(cancellationToken))
        {
            LibrarianItem<string>? item = await GetItemWithVersion(id, cancellationToken);
            if (item is not null && !await DeleteItemIfVersion(id, item.Version, cancellationToken))
                throw new LibrarianConcurrencyException($"Document '{id}' changed during clear.");
        }
    }

    public IQueryable<T> BuildQueryable<T>() => throw new NotSupportedException("R2 object storage has no native query engine; enumerate documents explicitly.");
    public ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default) => throw new NotSupportedException("R2 object storage has no atomic secondary indexes.");
    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException("R2 object storage has no atomic secondary indexes.");
    public ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) => throw new NotSupportedException("R2 object storage has no atomic secondary indexes.");
    public ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) => throw new NotSupportedException("R2 object storage has no atomic secondary indexes.");
    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null, bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException("R2 object storage has no atomic secondary indexes.");
    public void Dispose() => _disposed = true;
}
