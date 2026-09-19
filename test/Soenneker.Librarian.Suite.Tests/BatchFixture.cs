using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class BatchFixture : IAsyncDisposable
{
    private readonly IAsyncDisposable _owner;
    internal ILibrarianDatabase Database { get; }
    internal BatchFixture(string provider)
    {
        switch (provider)
        {
            case "memory":
                var memory = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
                Database = memory; _owner = memory; break;
            case "filesystem":
                var file = new PersistenceFixture();
                Database = file.Database; _owner = file; break;
            case "redis":
                var redis = new RedisPersistenceFixture();
                Database = redis.Database; _owner = redis; break;
            case "postgres":
                var postgres = new PostgresPersistenceFixture();
                Database = postgres.Database; _owner = postgres; break;
            default: throw new ArgumentException("Unknown provider.", nameof(provider));
        }
    }
    public ValueTask DisposeAsync() => _owner.DisposeAsync();
}
