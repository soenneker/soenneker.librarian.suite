using System;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Soenneker.Librarian.Mongo;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class MongoPersistenceFixture : IAsyncDisposable
{
    private readonly MongoClient _client;
    private readonly string _databaseName = "librarian_tests_" + Guid.NewGuid().ToString("N");
    internal MongoLibrarianDatabase Database { get; }
    internal IMongoCollection<BsonDocument> Store(string name) => _client.GetDatabase(_databaseName).GetCollection<BsonDocument>("librarian.test." + name);
    internal MongoLibrarianDatabase CreateDatabase() => new(_client.GetDatabase(_databaseName), "test");
    private MongoPersistenceFixture(string connectionString)
    {
        _client = new MongoClient(connectionString);
        Database = CreateDatabase();
    }
    internal static async Task<MongoPersistenceFixture> Create()
    {
        string? connectionString = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(connectionString)) Skip.Test("Set LIBRARIAN_TEST_MONGO to a replica set to run MongoDB integration tests.");
        var fixture = new MongoPersistenceFixture(connectionString!);
        try
        {
            if (Environment.GetEnvironmentVariable("LIBRARIAN_TEST_MONGO_INIT") == "1")
            {
                IMongoDatabase admin = fixture._client.GetDatabase("admin");
                try { await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument())); }
                catch (MongoCommandException exception) when (exception.Code == 23) { }
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
                    if (hello.GetValue("isWritablePrimary", false).ToBoolean()) return fixture;
                    await Task.Delay(250);
                }
                throw new TimeoutException("MongoDB replica set did not become writable.");
            }
            return fixture;
        }
        catch { fixture._client.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        try { await _client.DropDatabaseAsync(_databaseName); }
        finally { _client.Dispose(); }
    }
}
