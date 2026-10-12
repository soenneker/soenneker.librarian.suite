using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace Soenneker.Librarian.SurrealDb;

public sealed class SurrealDbLibrarianDatabase : ILibrarianDatabase
{
    private readonly ISurrealDbClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, SurrealDbLibrarianContainer> _containers = new(StringComparer.Ordinal);
    private volatile bool _disposed;
    internal string Key { get; }
    internal const string FailedCondition = "LIBRARIAN_CONDITION_FAILED_75CC7E";

    public SurrealDbLibrarianDatabase(IConfiguration configuration) : this(
        configuration["Librarian:SurrealDb:ConnectionString"] ?? throw new InvalidOperationException("Missing Librarian:SurrealDb:ConnectionString."),
        configuration["Librarian:SurrealDb:Key"] ?? "librarian") { }
    public SurrealDbLibrarianDatabase(string connectionString, string key = "librarian")
        : this(new SurrealDbClient(SurrealDbOptions.Create().FromConnectionString(connectionString).Build()), key) { _ownsClient = true; }
    public SurrealDbLibrarianDatabase(ISurrealDbClient client, string key = "librarian")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _client = client;
        Key = SurrealDbIndexValue.Hex(key);
    }
    internal void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); }
    internal string Table(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return "librarian_" + Key + "_" + SurrealDbIndexValue.Hex(name);
    }
    internal async ValueTask<SurrealDbResponse> Query(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        Check(token);
        return (await _client.RawQuery(sql, parameters, token).ConfigureAwait(false)).EnsureAllOks();
    }
    internal async ValueTask<bool> Transaction(string sql, Dictionary<string, object?> parameters, CancellationToken token)
    {
        Check(token);
        parameters["librarian_guard"] = Key;
        parameters["librarian_nonce"] = Guid.NewGuid().ToString("N");
        // Every write touches the same logical-database record. Snapshot conflicts also protect absent-key conditions.
        string script = "BEGIN TRANSACTION; UPSERT type::record('librarian_guards', $librarian_guard) SET revision=$librarian_nonce; " + sql + " COMMIT TRANSACTION; RETURN true;";
        SurrealDbResponse response = await _client.RawQuery(script, parameters, token).ConfigureAwait(false);
        if (response.Errors.OfType<SurrealDbErrorResult>().Any(error => error.Details == FailedCondition || error.Details == "An error occurred: " + FailedCondition)) return false;
        response.EnsureAllOks();
        if (response.Count == 0 || !response.GetValue<bool>(response.Count - 1)) throw new InvalidOperationException("SurrealDB did not confirm the commit.");
        return true;
    }
    internal static string Condition(string predicate) => "IF !(" + predicate + ") { THROW '" + FailedCondition + "'; }; ";

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        string table = Table(containerName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check(cancellationToken);
            if (!_containers.TryGetValue(containerName, out SurrealDbLibrarianContainer? container))
            {
                await Query("DEFINE TABLE IF NOT EXISTS librarian_guards SCHEMALESS; DEFINE TABLE IF NOT EXISTS " + table + " SCHEMALESS;", null, cancellationToken).ConfigureAwait(false);
                _containers.Add(containerName, container = new SurrealDbLibrarianContainer(this, table));
            }
            return container;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch.ValidateConcurrency(supportsVersions: true);
        Check(cancellationToken);
        if (batch.Writes.Count == 0) return true;
        var sql = new StringBuilder();
        var parameters = new Dictionary<string, object?>();
        int i = 0;
        foreach (LibrarianWrite write in batch.Writes)
        {
            var container = (SurrealDbLibrarianContainer)await GetContainer(write.Container, cancellationToken).ConfigureAwait(false);
            string record = container.Record(write.Id, "id" + i, parameters);
            if (write.CreateOnly) sql.Append(Condition($"(SELECT VALUE revision FROM {record}) = []"));
            if (write.ExpectedVersion is not null)
            {
                parameters["expected" + i] = write.ExpectedVersion;
                sql.Append(Condition($"(SELECT VALUE revision FROM {record}) = [$expected{i}]"));
            }
            sql.Append(container.Write(record, write.Id, write.Value, "w" + i, parameters));
            i++;
        }
        return await Transaction(sql.ToString(), parameters, cancellationToken).ConfigureAwait(false);
    }
    public ValueTask Save(CancellationToken cancellationToken = default) { Check(cancellationToken); return ValueTask.CompletedTask; }
    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default) => Save(cancellationToken);
    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Check(cancellationToken); if (!_containers.Remove(containerName, out var container)) return false; container.Dispose(); return true; }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var container in _containers.Values) container.Dispose();
            _containers.Clear();
            if (_ownsClient) await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
