using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Turso;

namespace Soenneker.Librarian.Turso;

public sealed class TursoLibrarianDatabase : ILibrarianDatabase
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, TursoLibrarianContainer> _containers = new(StringComparer.Ordinal);
    private bool _initialized;
    private volatile bool _disposed;
    internal string Key { get; }

    public TursoLibrarianDatabase(IConfiguration configuration) : this(
        configuration["Librarian:Turso:ConnectionString"] ?? throw new InvalidOperationException("Missing Librarian:Turso:ConnectionString."),
        configuration["Librarian:Turso:Key"] ?? "librarian") { }

    public TursoLibrarianDatabase(string connectionString, string key = "librarian")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var settings = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (settings.ContainsKey("Replica Path")) throw new NotSupportedException("Replica connections cannot guarantee authoritative conditional writes.");
        if (settings.TryGetValue("Data Source", out object? source) && source.ToString() == ":memory:")
            throw new ArgumentException("Use a persistent file or a remote database; private in-memory connections are not shared.", nameof(connectionString));
        _connectionString = connectionString;
        Key = TursoIndexValue.Hex(key);
    }

    internal void Check() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal DbCommand Command(DbConnection connection, string sql, params ReadOnlySpan<object> values)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (object value in values) command.Parameters.Add(new TursoParameter("$" + (command.Parameters.Count + 1), value));
        return command;
    }

    internal async ValueTask<DbConnection> Open(CancellationToken token)
    {
        Check();
        var connection = new TursoConnection(_connectionString);
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Check();
                if (!_initialized)
                {
                    await using DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                    foreach (string sql in Schema)
                    {
                        await using DbCommand command = Command(connection, sql);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    _initialized = true;
                }
            }
            finally { _gate.Release(); }
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal async ValueTask LockWrites(DbConnection connection, CancellationToken token)
    {
        // Acquire the database write lock before any condition reads. All provider writes use this path.
        await using DbCommand command = Command(connection,
            "INSERT INTO librarian_turso_databases(database_key,revision) VALUES($1,1) ON CONFLICT(database_key) DO UPDATE SET revision=revision+1", Key);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check();
            if (!_containers.TryGetValue(containerName, out TursoLibrarianContainer? container))
                _containers.Add(containerName, container = new TursoLibrarianContainer(containerName, this));
            return container;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using DbConnection connection = await Open(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockWrites(connection, cancellationToken).ConfigureAwait(false);
        foreach (LibrarianCondition condition in batch.Conditions)
        {
            using var container = new TursoLibrarianContainer(condition.Container, this);
            if (!string.Equals(await container.Read(connection, condition.Id, cancellationToken).ConfigureAwait(false), condition.ExpectedValue, StringComparison.Ordinal)) return false;
        }
        foreach (LibrarianWrite write in batch.Writes)
        {
            using var container = new TursoLibrarianContainer(write.Container, this);
            if (write.CreateOnly || write.ExpectedVersion is not null)
            {
                LibrarianItem<string>? current = await container.ReadVersion(connection, write.Id, cancellationToken).ConfigureAwait(false);
                if ((write.CreateOnly && current is not null) || (write.ExpectedVersion is not null && current?.Version != write.ExpectedVersion)) return false;
            }
            await container.Write(connection, write.Id, write.Value, "upsert", cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public ValueTask Save(CancellationToken cancellationToken = default) { Check(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default) => Save(cancellationToken);
    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Check(); if (!_containers.Remove(containerName, out TursoLibrarianContainer? container)) return false; container.Dispose(); return true; }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; foreach (var container in _containers.Values) container.Dispose(); _containers.Clear(); }
        finally { _gate.Release(); }
    }

    private static readonly string[] Schema =
    [
        "CREATE TABLE IF NOT EXISTS librarian_turso_databases(database_key TEXT PRIMARY KEY, revision INTEGER NOT NULL)",
        "CREATE TABLE IF NOT EXISTS librarian_turso_documents(database_key TEXT NOT NULL,container TEXT NOT NULL,id_key TEXT NOT NULL,original_id TEXT NOT NULL,document TEXT NOT NULL,revision TEXT NOT NULL,PRIMARY KEY(database_key,container,id_key))",
        "CREATE TABLE IF NOT EXISTS librarian_turso_indexes(database_key TEXT NOT NULL,container TEXT NOT NULL,path TEXT NOT NULL,PRIMARY KEY(database_key,container,path))",
        "CREATE TABLE IF NOT EXISTS librarian_turso_values(database_key TEXT NOT NULL,container TEXT NOT NULL,id_key TEXT NOT NULL,path TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(database_key,container,path,value,id_key))",
        "CREATE INDEX IF NOT EXISTS librarian_turso_values_document ON librarian_turso_values(database_key,container,id_key)"
    ];
}
