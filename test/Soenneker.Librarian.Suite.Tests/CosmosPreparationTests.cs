using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Moq;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

// SDK mocks exercise the provider adapter boundary; application services are not mocked.
public sealed class CosmosPreparationTests
{
    [Test]
    public async Task Async_queries_and_pages_preserve_options_and_enforce_partition_scope(CancellationToken cancellationToken)
    {
        // Constructing an SDK LINQ expression is local; all execution is intercepted at the SDK adapter boundary.
        using var client = new CosmosClient("https://localhost:8081", Convert.ToBase64String(new byte[64]));
        IOrderedQueryable<ExampleDocument> native = client.GetContainer("database", "items").GetItemLinqQueryable<ExampleDocument>();
        var store = new Mock<Container>(MockBehavior.Strict);
        var sdkDatabase = new Mock<Database>();
        sdkDatabase.SetupGet(value => value.Id).Returns("database");
        store.SetupGet(value => value.Id).Returns("items");
        store.SetupGet(value => value.Database).Returns(sdkDatabase.Object);
        store.Setup(value => value.GetItemLinqQueryable<ExampleDocument>(false, It.IsAny<string>(), It.IsAny<QueryRequestOptions>(), It.IsAny<CosmosLinqSerializerOptions>()))
            .Returns(native);
        var requests = new List<QueryRequestOptions>();
        store.Setup(value => value.GetItemQueryIterator<ExampleDocument>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition _, string _, QueryRequestOptions options) =>
            {
                requests.Add(options);
                var response = new Mock<FeedResponse<ExampleDocument>>();
                response.Setup(value => value.GetEnumerator()).Returns(() => new List<ExampleDocument> { new() { Id = "one", Name = "owned" } }.GetEnumerator());
                response.SetupGet(value => value.Headers).Returns(Response(HttpStatusCode.OK, "{}").Headers);
                response.SetupGet(value => value.StatusCode).Returns(HttpStatusCode.OK);
                var iterator = new Mock<FeedIterator<ExampleDocument>>();
                iterator.SetupSequence(value => value.HasMoreResults).Returns(true).Returns(false);
                iterator.Setup(value => value.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
                return iterator.Object;
            });
        var observer = new RecordingCosmosObserver();
        await using var database = new CosmosLibrarianDatabase(store.Object, options: new CosmosLibrarianOptions
        {
            QueryRequestOptionsFactory = _ => new QueryRequestOptions { PartitionKey = new PartitionKey("wrong"), SessionToken = "session", MaxConcurrency = 3, MaxItemCount = 77 },
            Observer = observer
        });
        IQueryable<ExampleDocument> query = (await database.GetContainer("items", "org", cancellationToken)).BuildQueryable<ExampleDocument>().Where(document => document.Name == "owned");
        Check((await query.ToList(cancellationToken)).Count == 1 && (await query.ToPage(2, cancellationToken: cancellationToken)).Items.Count == 1, "Async materialization failed.");
        Check(requests.Count == 2 && requests.All(options => options.PartitionKey == new PartitionKey("org") && options.SessionToken == "session" && options.MaxConcurrency == 3) &&
            requests[0].MaxItemCount == 77 && requests[1].MaxItemCount == 2 && observer.Responses.Count == 2, "Query options or diagnostics were lost.");
    }

    [Test]
    public async Task Resolver_supports_multiple_existing_containers_without_renaming_them(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var first = new Mock<Container>(MockBehavior.Strict);
        var second = new Mock<Container>(MockBehavior.Strict);
        await using var database = new CosmosLibrarianDatabase((name, token) =>
        {
            token.ThrowIfCancellationRequested();
            names.Add(name);
            return ValueTask.FromResult(name == "General" ? first.Object : second.Object);
        });
        ILibrarianContainer general = await database.GetContainer("General", cancellationToken);
        await database.GetContainer("Telephony", cancellationToken);
        Check(ReferenceEquals(general, await database.GetContainer("General", cancellationToken)) && names.SequenceEqual(new[] { "General", "Telephony" }),
            "Resolver renamed containers or failed to cache them.");
    }

    private static ResponseMessage Response(HttpStatusCode status, string json)
    {
        var response = new ResponseMessage(status) { Content = new MemoryStream(Encoding.UTF8.GetBytes(json)) };
        response.Headers.Add("etag", "new-version");
        response.Headers.Add("x-ms-request-charge", "3.5");
        response.Headers.Add("x-ms-session-token", "session");
        return response;
    }

    [Test]
    public async Task Options_preserve_explicit_versions_and_observers_cannot_change_commit_outcomes(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(value => value.Id).Returns("items");
        store.Setup(value => value.ReplaceItemStreamAsync(It.IsAny<Stream>(), "one", new PartitionKey("org"),
            It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "original" && options.SessionToken == "session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.OK, "{}"));
        var observer = new RecordingCosmosObserver { Throw = true };
        await using var database = new CosmosLibrarianDatabase(store.Object, options: new CosmosLibrarianOptions
        {
            ItemRequestOptionsFactory = (_, _) => new ItemRequestOptions { IfMatchEtag = "wrong", SessionToken = "session" }, Observer = observer
        });
        ILibrarianContainer container = await database.GetContainer("items", "org", cancellationToken);
        LibrarianItem<string>? result = await container.UpdateItemIfVersion("one", NativeDocumentJson.Create("one", "{}", "org"), "original", cancellationToken);
        Check(result?.Version == "new-version" && observer.Responses.Count == 1 && observer.Responses[0].RequestCharge == 3.5 &&
            observer.Responses[0].SessionToken == "session", "Options or telemetry failed.");
        store.VerifyAll();
    }

    [Test]
    public async Task Patches_preserve_scope_strip_metadata_and_reject_identity_changes(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(value => value.Id).Returns("items");
        store.Setup(value => value.PatchItemStreamAsync("one", new PartitionKey("org"), It.IsAny<IReadOnlyList<PatchOperation>>(),
            It.Is<PatchItemRequestOptions>(options => options.FilterPredicate == "FROM c WHERE c._etag = \"old\"" && options.IfMatchEtag == null && options.EnableContentResponseOnWrite == true), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.OK, "{\"id\":\"one\",\"partitionKey\":\"org\",\"value\":2,\"_etag\":\"new-version\"}"));
        await using var database = new CosmosLibrarianDatabase(store.Object);
        ICosmosLibrarianContainer container = await database.GetCosmosContainer("items", "org", cancellationToken);
        LibrarianItem<string>? result = await container.PatchItem("one", [PatchOperation.Set("/value", 2)], "old", cancellationToken);
        Check(result?.Version == "new-version" && !result.Document.Contains("_etag"), "Patch result lost version or retained metadata.");
        foreach (string path in new[] { "/id", "/partitionKey", "/_etag", "/" })
        {
            try { await container.PatchItem("one", [PatchOperation.Set(path, "bad")], cancellationToken: cancellationToken); throw new Exception("Protected patch accepted."); }
            catch (ArgumentException) { }
        }
        store.VerifyAll();
    }

    [Test]
    public async Task Explicit_index_management_merges_without_overwriting_container_settings(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(value => value.Id).Returns("items");
        var properties = new ContainerProperties("items", "/partitionKey") { DefaultTimeToLive = 123 };
        var current = new Mock<ContainerResponse>();
        current.SetupGet(value => value.Resource).Returns(properties);
        current.SetupGet(value => value.ETag).Returns("policy-version");
        store.Setup(value => value.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(current.Object);
        store.Setup(value => value.ReplaceContainerAsync(properties, It.Is<ContainerRequestOptions>(options => options.IfMatchEtag == "policy-version"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(current.Object);
        await using var database = new CosmosLibrarianDatabase(store.Object);
        IReadOnlyList<IReadOnlyList<CompositePath>> composites = [new[] { new CompositePath { Path = "/name" }, new CompositePath { Path = "/createdAt", Order = CompositePathSortOrder.Descending } }];
        await database.EnsureIndexes("items", ["name"], composites, cancellationToken);
        await database.EnsureIndexes("items", ["name"], composites, cancellationToken);
        Check(properties.DefaultTimeToLive == 123 && properties.PartitionKeyPath == "/partitionKey" && properties.IndexingPolicy.CompositeIndexes.Count == 1,
            "Index merge changed unrelated settings or duplicated an index.");
        store.Verify(value => value.ReplaceContainerAsync(properties, It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Index_policy_conflicts_refresh_before_retrying_and_preserve_concurrent_changes(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(value => value.Id).Returns("items");
        var original = new Mock<ContainerResponse>();
        original.SetupGet(value => value.Resource).Returns(new ContainerProperties("items", "/partitionKey"));
        original.SetupGet(value => value.ETag).Returns("old");
        var concurrent = new ContainerProperties("items", "/partitionKey") { DefaultTimeToLive = 456 };
        var refreshed = new Mock<ContainerResponse>();
        refreshed.SetupGet(value => value.Resource).Returns(concurrent);
        refreshed.SetupGet(value => value.ETag).Returns("new");
        store.SetupSequence(value => value.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(original.Object).ReturnsAsync(refreshed.Object);
        store.Setup(value => value.ReplaceContainerAsync(It.IsAny<ContainerProperties>(), It.Is<ContainerRequestOptions>(options => options.IfMatchEtag == "old"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("Changed", HttpStatusCode.PreconditionFailed, 0, "activity", 1));
        store.Setup(value => value.ReplaceContainerAsync(concurrent, It.Is<ContainerRequestOptions>(options => options.IfMatchEtag == "new"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshed.Object);
        await using var database = new CosmosLibrarianDatabase(store.Object);
        await database.EnsureIndexes("items", ["name"], cancellationToken: cancellationToken);
        Check(concurrent.DefaultTimeToLive == 456 && concurrent.IndexingPolicy.IncludedPaths.Any(path => path.Path == "/name/?"), "Retry overwrote concurrent container settings.");
        store.VerifyAll();
    }
}
