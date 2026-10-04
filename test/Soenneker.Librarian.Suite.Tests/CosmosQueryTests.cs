using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Moq;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosQueryTests
{
    [Test]
    public async Task EnsureIndex_validates_without_reading_or_changing_the_policy()
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        await using var database = new CosmosLibrarianDatabase(store.Object);
        ILibrarianContainer items = await database.GetContainer("items");
        await items.EnsureIndex("details.region");
        try { await items.EnsureIndex("details..region"); throw new Exception("Invalid path accepted."); } catch (ArgumentException) { }
        try { await items.EnsureIndex("name", new CancellationToken(true)); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        store.VerifyGet(c => c.Id, Times.Once);
        store.VerifyNoOtherCalls();
    }
    [Test]
    public async Task Equality_and_range_pages_do_not_require_composite_ordering_or_policy_changes()
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        var queries = new List<string>();
        store.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions options) =>
            {
                queries.Add(query.QueryText);
                Check(options.PartitionKey == new PartitionKey("org-a"), "Query lost partition scope.");
                var iterator = new Mock<FeedIterator>();
                iterator.SetupSequence(i => i.HasMoreResults).Returns(true).Returns(false);
                iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ResponseMessage(HttpStatusCode.OK)
                { Content = new MemoryStream(Encoding.UTF8.GetBytes("{\"Documents\":[]}")) });
                return iterator.Object;
            });
        await using var database = new CosmosLibrarianDatabase(store.Object);
        ILibrarianContainer items = await database.GetContainer("items", "org-a");
        await items.FindRangeByIndex<NativeDocument>("score_value", 1, 3, descending: true);
        await items.FindByIndex<NativeDocument>("score_value", 2);
        Check(queries[0].Contains("ORDER BY c[\"score_value\"] DESC OFFSET") && queries[1].Contains("ORDER BY c.id ASC OFFSET"), "Queries still inject composite ordering.");
        store.Verify(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Exactly(2));
        store.VerifyGet(c => c.Id, Times.Once);
        store.VerifyNoOtherCalls();
    }
}
