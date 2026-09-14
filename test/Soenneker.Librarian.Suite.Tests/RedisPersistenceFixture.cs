using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Redis;
using Soenneker.Redis.Client;
using StackExchange.Redis;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class RedisPersistenceFixture : IAsyncDisposable
{
    public string Key { get; } = $"librarian-tests-{Guid.NewGuid():N}";
    public IConfiguration Configuration { get; }
    public RedisClient Client { get; }
    public RedisLibrarianDatabase Database { get; }
    public string KeyPrefix { get; }
    public string RedisPrefix => KeyPrefix + ":{" + Key + "}:containers:";

    public RedisPersistenceFixture(string keyPrefix = "librarian")
    {
        KeyPrefix = keyPrefix;
        string? connectionString = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connectionString))
            Skip.Test("Set LIBRARIAN_TEST_REDIS to run Redis integration tests.");
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:Redis:ConnectionString"] = connectionString,
            ["Librarian:Redis:Key"] = Key,
            ["Librarian:Redis:KeyPrefix"] = KeyPrefix
        }).Build();
        Client = new RedisClient(Configuration, NullLogger<RedisClient>.Instance);
        Database = CreateDatabase();
    }

    public RedisLibrarianDatabase CreateDatabase() => new(Configuration, Client, NullLogger<RedisLibrarianDatabase>.Instance);

    public async ValueTask<IDatabase> GetStore() => (await Client.Get()).GetDatabase();

    public async ValueTask DisposeAsync()
    {
        try { await Database.DisposeAsync(); }
        finally
        {
            try
            {
                ConnectionMultiplexer connection = await Client.Get();
                IDatabase store = connection.GetDatabase();
                // Cleanup only this fixture's database hash tag, including any container names used by batch tests.
                foreach (EndPoint endpoint in connection.GetEndPoints())
                    await foreach (RedisKey redisKey in connection.GetServer(endpoint).KeysAsync(pattern: RedisPrefix + "*"))
                        await store.KeyDeleteAsync(redisKey);
            }
            finally { await Client.DisposeAsync(); }
        }
    }
}
