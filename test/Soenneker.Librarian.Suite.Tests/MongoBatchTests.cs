using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Mongo;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class MongoBatchTests
{
    private static (MongoLibrarianDatabase Database, Mock<IMongoCollection<BsonDocument>> Collection, Mock<IClientSessionHandle> Session) Create()
    {
        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection.Setup(c => c.WithReadConcern(It.IsAny<ReadConcern>())).Returns(collection.Object);
        collection.Setup(c => c.WithReadPreference(It.IsAny<ReadPreference>())).Returns(collection.Object);
        collection.Setup(c => c.WithWriteConcern(It.IsAny<WriteConcern>())).Returns(collection.Object);
        var session = new Mock<IClientSessionHandle>();
        session.SetupGet(s => s.IsInTransaction).Returns(true);
        var client = new Mock<IMongoClient>();
        client.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        var store = new Mock<IMongoDatabase>();
        store.SetupGet(d => d.Client).Returns(client.Object);
        store.Setup(d => d.GetCollection<BsonDocument>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>())).Returns(collection.Object);
        return (new MongoLibrarianDatabase(store.Object, "test"), collection, session);
    }
    [Test]
    public async Task Unmatched_version_aborts_without_applying_later_writes(CancellationToken cancellationToken)
    {
        (MongoLibrarianDatabase database, Mock<IMongoCollection<BsonDocument>> collection, Mock<IClientSessionHandle> session) = Create();
        await using MongoLibrarianDatabase owned = database;
        collection.Setup(c => c.ReplaceOneAsync(session.Object, It.IsAny<FilterDefinition<BsonDocument>>(), It.IsAny<BsonDocument>(), It.IsAny<ReplaceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplaceOneResult.Acknowledged(0, 0, null));
        Check(!await database.Execute(new LibrarianBatch([
            new LibrarianWrite("items", "a", NativeDocumentJson.Create("a"), "stale"),
            new LibrarianWrite("items", "b", NativeDocumentJson.Create("b"))]), cancellationToken: cancellationToken), "Stale batch committed.");
        session.Verify(s => s.AbortTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        collection.Verify(c => c.ReplaceOneAsync(session.Object, It.IsAny<FilterDefinition<BsonDocument>>(), It.IsAny<BsonDocument>(), It.IsAny<ReplaceOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Test]
    public async Task Uncertain_commit_is_not_replayed(CancellationToken cancellationToken)
    {
        (MongoLibrarianDatabase database, Mock<IMongoCollection<BsonDocument>> collection, Mock<IClientSessionHandle> session) = Create();
        await using MongoLibrarianDatabase owned = database;
        collection.Setup(c => c.ReplaceOneAsync(session.Object, It.IsAny<FilterDefinition<BsonDocument>>(), It.IsAny<BsonDocument>(), It.IsAny<ReplaceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplaceOneResult.Acknowledged(1, 1, null));
        session.Setup(s => s.CommitTransactionAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("Uncertain commit"));
        try { await database.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", NativeDocumentJson.Create("a"), "version")]), cancellationToken: cancellationToken); throw new Exception("Commit failure hidden."); }
        catch (TimeoutException) { }
        collection.Verify(c => c.ReplaceOneAsync(session.Object, It.IsAny<FilterDefinition<BsonDocument>>(), It.IsAny<BsonDocument>(), It.IsAny<ReplaceOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.AbortTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
