using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Moq;
using Soenneker.Librarian.Cosmos;
using Soenneker.Librarian.Abstractions.Queries;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosAllocationTests
{
    [Test]
    public async Task Factory_recovers_after_failed_initialization_and_transient_reads(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        fixture.Containers.SetupSequence(c => c.Get("items", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provisioning temporarily unavailable"))
            .ReturnsAsync(fixture.Store.Object);
        var factory = fixture.Resolve<ICosmosLibrarianDatabaseFactory>();
        bool failed = false;
        try { await factory.Open("items", cancellationToken: cancellationToken); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "Initialization failure was hidden.");
        var store = await factory.Open("items", cancellationToken: cancellationToken);
        fixture.Store.SetupSequence(c => c.ReadItemStreamAsync("one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResponseMessage(HttpStatusCode.ServiceUnavailable))
            .ReturnsAsync(Response("{\"id\":\"one\",\"partitionKey\":\"org\",\"score_value\":17}"));
        failed = false;
        try { await store.ReadDocument<NativeDocument>("org:one", cancellationToken); }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.ServiceUnavailable) { failed = true; }
        Check(failed, "An exhausted SDK failure was hidden.");
        Check((await store.ReadDocument<NativeDocument>("org:one", cancellationToken))?.Document.Score == 17, "A transient failure poisoned the cached handle.");
        fixture.Containers.Verify(c => c.Get("items", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task Conditional_patch_rejects_missing_version_before_IO(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        bool rejected = false;
        try { await fixture.Resolve<CosmosAdapterRepository>().PatchItemIfMatch("org:one", [PatchOperation.Set("/name", "changed")], null!, cancellationToken); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Missing version became an unconditional write.");
        fixture.Containers.Verify(c => c.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Native_repository_writes_and_patches_preserve_versions_and_serializer_contract(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        var repository = fixture.Resolve<CosmosAdapterRepository>();
        var document = new NativeDocument { DocumentId = "one", PartitionKey = "org", Name = "雪😀", Score = 7 };
        fixture.Store.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream stream, PartitionKey _, ItemRequestOptions options, CancellationToken _) =>
            {
                using var body = JsonDocument.Parse(stream);
                Check(body.RootElement.GetProperty("score_value").GetInt32() == 7 && body.RootElement.GetProperty("name").GetString() == document.Name,
                    "Native write ignored serializer mappings.");
                Check(options.EnableContentResponseOnWrite == false, "Unused response was requested.");
                return new ResponseMessage(HttpStatusCode.Created);
            });
        await repository.AddItem(document, cancellationToken: cancellationToken);
        fixture.Store.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), "one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string _, PartitionKey _, ItemRequestOptions options, CancellationToken _) =>
            {
                Check(options.IfMatchEtag == "original", "Conditional write lost its precondition.");
                return Response("{}", "new-version");
            });
        var replaced = await repository.UpdateItemIfMatch(document, "original", cancellationToken);
        Check(ReferenceEquals(replaced.Document, document) && replaced.ETag == "new-version", "Replacement lost its version or needlessly copied the document.");
        fixture.Store.Setup(c => c.PatchItemStreamAsync("one", new PartitionKey("org"), It.IsAny<System.Collections.Generic.IReadOnlyList<PatchOperation>>(),
            It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Response("{\"id\":\"one\",\"partitionKey\":\"org\",\"score_value\":9}", "patch-version"));
        var patched = await repository.PatchItemIfMatch("org:one", [PatchOperation.Set("/score_value", 9)], "new-version", cancellationToken);
        Check(patched.Document.Score == 9 && patched.ETag == "patch-version", "Native patch lost content or version.");
    }

    [Test]
    public async Task Option_views_share_physical_containers_and_follow_owner_disposal(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        var factory = fixture.Resolve<ICosmosLibrarianDatabaseFactory>();
        ICosmosLibrarianContainer? view = null;
        for (int i = 0; i < 100; i++)
            view = await factory.Open("items", new Soenneker.Cosmos.Repository.Dtos.CosmosReadOptions { SessionToken = i.ToString() }, cancellationToken: cancellationToken);
        fixture.Containers.Verify(c => c.Get("items", It.IsAny<CancellationToken>()), Times.Once);
        await factory.DisposeAsync();
        bool rejected = false;
        try { view!.DeserializeDocument<JsonElement>("{}"); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "Options view survived its factory.");
        rejected = false;
        try { await factory.Open("items", cancellationToken: cancellationToken); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "Disposed factory reopened a container.");
    }

    [Test]
    public async Task Pooled_fingerprints_resume_and_reject_other_queries(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        fixture.Store.Setup(c => c.GetItemLinqQueryable<NativeDocument>(false, It.IsAny<string>(), It.IsAny<QueryRequestOptions>(), It.IsAny<CosmosLinqSerializerOptions>()))
            .Returns(fixture.Native.GetItemLinqQueryable<NativeDocument>());
        fixture.Store.Setup(c => c.GetItemQueryIterator<NativeDocument>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition _, string token, QueryRequestOptions _) =>
            {
                Check(token is null or "native-next", "The wrapped token was not decoded.");
                var page = new Mock<FeedResponse<NativeDocument>>();
                page.SetupGet(p => p.ContinuationToken).Returns(token is null ? "native-next" : null!);
                page.Setup(p => p.GetEnumerator()).Returns(() => Enumerable.Empty<NativeDocument>().GetEnumerator());
                var iterator = new Mock<FeedIterator<NativeDocument>>();
                iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(page.Object);
                return iterator.Object;
            });
        var store = await fixture.Resolve<ICosmosLibrarianDatabaseFactory>().Open("items", cancellationToken: cancellationToken);
        var query = store.BuildQueryable<NativeDocument>().Where(d => d.Score == 7);
        var first = await query.ToPage(10, cancellationToken: cancellationToken);
        Check(first.ContinuationToken is not null, "Continuation token was lost.");
        var last = await query.ToPage(10, first.ContinuationToken, cancellationToken);
        Check(last.ContinuationToken is null, "Terminal page returned a continuation.");
        bool rejected = false;
        try { await store.BuildQueryable<NativeDocument>().Where(d => d.Score == 8).ToPage(10, first.ContinuationToken, cancellationToken); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "A continuation was accepted by another query.");
    }

    private static ResponseMessage Response(string json, string version = "header-version")
    {
        var response = new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(Encoding.UTF8.GetBytes(json)) };
        response.Headers.Add("etag", version);
        return response;
    }

    [Test]
    public async Task Typed_reads_restore_authoritative_metadata_without_touching_nested_fields(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        const string json = "{\"id\":\"one\",\"partitionKey\":\"org\",\"_etag\":\"stale\",\"_rid\":\"internal\",\"nested\":{\"_etag\":\"keep\"}}";
        fixture.Store.Setup(c => c.ReadItemStreamAsync("one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(json));
        var store = await fixture.Resolve<ICosmosLibrarianDatabaseFactory>().Open("items", cancellationToken: cancellationToken);
        var raw = (await store.GetItemWithVersion("org:one", cancellationToken))!;
        var item = new Soenneker.Librarian.Abstractions.LibrarianItem<JsonElement>(store.DeserializeDocument<JsonElement>(raw.Document, raw.Version), raw.Version);
        Check(item is not null && item.Version == "header-version" && item.Document.GetProperty("_etag").GetString() == item.Version,
            "The response version must override a stale body version.");
        Check(!item!.Document.TryGetProperty("_rid", out _) && item.Document.GetProperty("nested").GetProperty("_etag").GetString() == "keep",
            "Metadata filtering changed nested application fields.");
        JsonElement restored = store.DeserializeDocument<JsonElement>(json, "override\"雪");
        Check(restored.GetProperty("_etag").GetString() == "override\"雪" && restored.GetProperty("_rid").GetString() == "internal",
            "Deserialization must only replace ETag and escape its value.");
    }

    [Test]
    public async Task Typed_bulk_reads_preserve_missing_slots_order_and_independent_duplicates(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        fixture.Store.Setup(c => c.ReadManyItemsAsync<NativeDocument>(It.IsAny<System.Collections.Generic.IReadOnlyList<(string, PartitionKey)>>(),
                It.IsAny<ReadManyRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                NativeDocument[] rows = [new() { DocumentId = "two", PartitionKey = "org", Score = 2 }, new() { DocumentId = "one", PartitionKey = "org", Score = 1 }];
                var response = new Mock<FeedResponse<NativeDocument>>();
                response.Setup(r => r.GetEnumerator()).Returns(() => ((System.Collections.Generic.IEnumerable<NativeDocument>)rows).GetEnumerator());
                return response.Object;
            });
        var store = await fixture.Resolve<ICosmosLibrarianDatabaseFactory>().Open("items", cancellationToken: cancellationToken);
        NativeDocument?[] items = await store.ReadDocuments<NativeDocument>(["org:one", "org:missing", "org:two", "org:one"], cancellationToken);
        Check(items.Length == 4 && items[0]?.Score == 1 && items[1] is null && items[2]?.Score == 2 && items[3]?.Score == 1,
            "Bulk reads lost ordering or missing slots.");
        items[0]!.Score = 9;
        Check(items[3]!.Score == 1, "Duplicate identities share mutable objects.");
    }

    [Test]
    public async Task Pooled_buffers_round_trip_large_unicode_documents_and_release_request_streams(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        var store = await fixture.Resolve<ICosmosLibrarianDatabaseFactory>().Open("items", cancellationToken: cancellationToken);
        string payload = string.Concat(System.Linq.Enumerable.Repeat("雪😀\\\"", 20000));
        var document = new NativeDocument { DocumentId = "one", PartitionKey = "org", Name = payload, Score = 7 };
        string json = store.SerializeDocument(document);
        Check(store.DeserializeDocument<NativeDocument>(json, "version").Name == payload, "A growing pooled buffer corrupted Unicode.");
        Stream? captured = null;
        fixture.Store.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream stream, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
            {
                captured = stream;
                using var parsed = JsonDocument.Parse(stream);
                Check(parsed.RootElement.GetProperty("name").GetString() == payload, "Request bytes changed.");
                return new ResponseMessage(HttpStatusCode.Created);
            });
        await store.AddItem("org:one", json, cancellationToken);
        Check(captured is { CanRead: false }, "The request stream outlived its operation.");
        Check(store.DeserializeDocument<NativeDocument>("{\"id\":\"small\",\"partitionKey\":\"small\",\"name\":\"tiny\"}").Name == "tiny",
            "Reused buffers exposed stale bytes.");
    }

    [Test]
    public async Task Cached_handles_are_shared_and_unload_invalidates_them(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        await using var database = new CosmosLibrarianDatabase(fixture.Store.Object);
        var first = await database.GetCosmosContainer("items", cancellationToken: cancellationToken);
        var tasks = new Task<ICosmosLibrarianContainer>[64];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(async () => await database.GetCosmosContainer("items", cancellationToken: cancellationToken), cancellationToken);
        foreach (var item in await Task.WhenAll(tasks)) Check(ReferenceEquals(first, item), "Cached handles differ.");
        await database.UnloadContainer("items", cancellationToken);
        var next = await database.GetCosmosContainer("items", cancellationToken: cancellationToken);
        Check(!ReferenceEquals(first, next), "Unload retained the old handle.");
        bool disposed = false;
        try { first.DeserializeDocument<JsonElement>("{}"); }
        catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "Unloaded handles remain usable.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool rejected = false;
        try { await database.GetCosmosContainer("items", cancellationToken: canceled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "The cache fast path ignored cancellation.");
    }
}
