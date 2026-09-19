using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Soenneker.Extensions.Configuration;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Postgres;

public sealed class PostgresLibrarianDatabase : ILibrarianDatabase
{
    private readonly NpgsqlDataSource _source;
    private readonly bool _ownsSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PostgresLibrarianContainer> _containers = new(StringComparer.Ordinal);
    private volatile bool _initialized;
    private volatile bool _disposed;
    internal string Key { get; }

    public PostgresLibrarianDatabase(IConfiguration configuration)
        : this(configuration.GetValueStrict<string>("Librarian:Postgres:ConnectionString"),
            configuration.GetValueStrict<string>("Librarian:Postgres:Key")) { }

    /// <summary>Creates a provider owning its connection pool. The key isolates a logical Librarian database.</summary>
    public PostgresLibrarianDatabase(string connectionString, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = PostgresIndexValue.Hex(key);
        _source = NpgsqlDataSource.Create(connectionString);
        _ownsSource = true;
    }

    /// <summary>Uses a caller-owned connection pool, which remains open after provider disposal.</summary>
    public PostgresLibrarianDatabase(NpgsqlDataSource dataSource, string key)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _source = dataSource;
        Key = PostgresIndexValue.Hex(key);
    }

    internal void Check() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal async ValueTask<NpgsqlConnection> Open(CancellationToken token)
    {
        Check();
        token.ThrowIfCancellationRequested();
        if (!_initialized)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Check();
                if (!_initialized)
                {
                    await using NpgsqlConnection connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
                    await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                    // Serialize first-time DDL across processes; released on commit or rollback.
                    await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(-704382180949935101)", connection, transaction))
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    await using (var command = new NpgsqlCommand(Schema, connection, transaction))
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    _initialized = true;
                }
            }
            finally { _gate.Release(); }
        }
        Check();
        return await _source.OpenConnectionAsync(token).ConfigureAwait(false);
    }

    internal NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (object value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }

    internal async ValueTask LockWrites(NpgsqlConnection connection, CancellationToken token)
    {
        await using (NpgsqlCommand command = Command(connection,
            "INSERT INTO public.librarian_postgres_databases (database_key) VALUES ($1) ON CONFLICT DO NOTHING", Key))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await using (NpgsqlCommand command = Command(connection,
            "SELECT database_key FROM public.librarian_postgres_databases WHERE database_key=$1 FOR UPDATE", Key))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check();
            if (!_containers.TryGetValue(containerName, out PostgresLibrarianContainer? container))
                _containers.Add(containerName, container = new PostgresLibrarianContainer(containerName, this));
            return container;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using NpgsqlConnection connection = await Open(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockWrites(connection, cancellationToken).ConfigureAwait(false);
        foreach (LibrarianCondition condition in batch.Conditions)
        {
            using var container = new PostgresLibrarianContainer(condition.Container, this);
            string? current = await container.Read(connection, condition.Id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, condition.ExpectedValue, StringComparison.Ordinal)) return false;
        }
        foreach (LibrarianWrite write in batch.Writes)
        {
            using var container = new PostgresLibrarianContainer(write.Container, this);
            await container.Write(connection, write.Id, write.Value, "upsert", cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public ValueTask Save(CancellationToken cancellationToken = default)
    {
        Check();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default)
    {
        Check();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check();
            if (!_containers.Remove(containerName, out PostgresLibrarianContainer? container)) return false;
            container.Dispose();
            return true;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (PostgresLibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
            if (_ownsSource) await _source.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS public.librarian_postgres_databases (
            database_key text COLLATE "C" PRIMARY KEY
        );
        CREATE TABLE IF NOT EXISTS public.librarian_postgres_documents (
            database_key text COLLATE "C" NOT NULL,
            container text COLLATE "C" NOT NULL,
            id_key text COLLATE "C" NOT NULL,
            original_id text NOT NULL,
            document text NOT NULL,
            body jsonb,
            PRIMARY KEY (database_key, container, id_key)
        );
        CREATE TABLE IF NOT EXISTS public.librarian_postgres_indexes (
            database_key text COLLATE "C" NOT NULL,
            container text COLLATE "C" NOT NULL,
            path text COLLATE "C" NOT NULL,
            PRIMARY KEY (database_key, container, path)
        );
        CREATE TABLE IF NOT EXISTS public.librarian_postgres_values (
            database_key text COLLATE "C" NOT NULL,
            container text COLLATE "C" NOT NULL,
            path text COLLATE "C" NOT NULL,
            id_key text COLLATE "C" NOT NULL,
            value text COLLATE "C" NOT NULL,
            PRIMARY KEY (database_key, container, path, id_key),
            FOREIGN KEY (database_key, container, id_key)
                REFERENCES public.librarian_postgres_documents ON DELETE CASCADE,
            FOREIGN KEY (database_key, container, path)
                REFERENCES public.librarian_postgres_indexes ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS librarian_postgres_values_ordered
            ON public.librarian_postgres_values (database_key, container, path, value, id_key);
        CREATE INDEX IF NOT EXISTS librarian_postgres_values_document
            ON public.librarian_postgres_values (database_key, container, id_key);
        """;
}
