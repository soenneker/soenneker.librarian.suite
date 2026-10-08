using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Mongo;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class MongoIndexTests
{
    [Test]
    public async Task Container_access_and_queries_only_create_indexes_when_explicitly_requested(CancellationToken cancellationToken)
    {
        var store = new Mock<IMongoDatabase>(MockBehavior.Strict);
        store.SetupGet(d => d.Client).Returns(Mock.Of<IMongoClient>());
        var collection = new Mock<IMongoCollection<BsonDocument>>(MockBehavior.Strict);
        collection.Setup(c => c.WithReadConcern(It.IsAny<ReadConcern>())).Returns(collection.Object);
        collection.Setup(c => c.WithReadPreference(It.IsAny<ReadPreference>())).Returns(collection.Object);
        collection.Setup(c => c.WithWriteConcern(It.IsAny<WriteConcern>())).Returns(collection.Object);
        store.Setup(d => d.GetCollection<BsonDocument>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>())).Returns(collection.Object);
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        cursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        collection.Setup(c => c.FindAsync<BsonDocument>(It.IsAny<FilterDefinition<BsonDocument>>(), It.IsAny<FindOptions<BsonDocument, BsonDocument>>(), It.IsAny<CancellationToken>())).ReturnsAsync(cursor.Object);
        await using var database = new MongoLibrarianDatabase(store.Object, "test");
        ILibrarianContainer items = await database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
        store.Verify(d => d.GetCollection<BsonDocument>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()), Times.Never);
        await items.FindRangeByIndex<NativeDocument>("score_value", 1, 2, cancellationToken: cancellationToken);
        await items.FindByIndex<NativeDocument>("score_value", 1, cancellationToken: cancellationToken);
        collection.VerifyGet(c => c.Indexes, Times.Never);

        var indexes = new Mock<IMongoIndexManager<BsonDocument>>(MockBehavior.Strict);
        indexes.Setup(i => i.CreateOneAsync(It.IsAny<CreateIndexModel<BsonDocument>>(), It.IsAny<CreateOneIndexOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync("score_value_1__id_1");
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);
        await items.EnsureIndex("score_value", cancellationToken: cancellationToken);
        indexes.Verify(i => i.CreateOneAsync(It.IsAny<CreateIndexModel<BsonDocument>>(), It.IsAny<CreateOneIndexOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
