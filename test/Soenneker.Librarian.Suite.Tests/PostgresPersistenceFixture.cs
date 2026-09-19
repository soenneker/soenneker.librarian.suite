using System;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Librarian.Postgres;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class PostgresPersistenceFixture : IAsyncDisposable
{
    public string Key { get; } = $"librarian-tests-{Guid.NewGuid():N}";
    public NpgsqlDataSource Source { get; }
    public PostgresLibrarianDatabase Database { get; }

    public PostgresPersistenceFixture()
    {
        string? connectionString = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) Skip.Test("Set LIBRARIAN_TEST_POSTGRES to run PostgreSQL integration tests.");
        Source = NpgsqlDataSource.Create(connectionString!);
        Database = CreateDatabase();
    }

    public PostgresLibrarianDatabase CreateDatabase() => new(Source, Key);

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        try
        {
            // Scope cleanup to this fixture's logical database, including batch-created containers.
            string encoded = string.Concat(System.Linq.Enumerable.Select(Key, character => ((int)character).ToString("X4")));
            foreach (string table in new[] { "documents", "indexes", "databases" })
            {
                await using NpgsqlCommand command = Source.CreateCommand($"DELETE FROM public.librarian_postgres_{table} WHERE database_key=$1");
                command.Parameters.AddWithValue(encoded);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally { await Source.DisposeAsync(); }
    }
}
