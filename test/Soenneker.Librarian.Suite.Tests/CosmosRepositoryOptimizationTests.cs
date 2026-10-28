using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Moq;
using Moq.Protected;
using Soenneker.Cosmos.Repository.Dtos;
using Soenneker.Dtos.IdPartitionPair;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosRepositoryOptimizationTests
{
    private static ResponseMessage Response(HttpStatusCode status, string? json = null, string? etag = null)
    {
        var response = new ResponseMessage(status);
        if (json is not null) response.Content = new MemoryStream(Encoding.UTF8.GetBytes(json));
        if (etag is not null) response.Headers.Add("etag", etag);
        return response;
    }

    private static FeedIterator<T> Page<T>(T[] values, string? continuation = null)
    {
        bool read = false;
        var iterator = new Mock<FeedIterator<T>>();
        iterator.SetupGet(i => i.HasMoreResults).Returns(() => !read);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            read = true;
            var response = new Mock<FeedResponse<T>>();
            response.SetupGet(r => r.Count).Returns(values.Length);
            response.SetupGet(r => r.Resource).Returns(values);
            response.SetupGet(r => r.ContinuationToken).Returns(continuation!);
            response.Setup(r => r.GetEnumerator()).Returns(() => ((IEnumerable<T>)values).GetEnumerator());
            return response.Object;
        });
        return iterator.Object;
    }

    [Test]
    public async Task Bulk_pairs_use_native_read_many_and_reuse_default_container(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        fixture.Store.Setup(c => c.ReadManyItemsAsync<NativeDocument>(It.IsAny<IReadOnlyList<(string, PartitionKey)>>(), It.IsAny<ReadManyRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(string, PartitionKey)> addresses, ReadManyRequestOptions _, CancellationToken _) =>
            {
                Check(addresses.Count == 2 && addresses[0] == ("one", new PartitionKey("org")), "Bulk identities were not deduplicated.");
                NativeDocument[] documents = [new() { DocumentId = "one", PartitionKey = "org", Name = "found" }];
                var response = new Mock<FeedResponse<NativeDocument>>();
                response.Setup(r => r.GetEnumerator()).Returns(() => ((IEnumerable<NativeDocument>)documents).GetEnumerator());
                return response.Object;
            });
        CosmosAdapterRepository repository = fixture.Resolve<CosmosAdapterRepository>();
        List<NativeDocument> result = await repository.GetAllByIdPartitionPairs([
            new IdPartitionPair { Id = "one", PartitionKey = "org" },
            new IdPartitionPair { Id = "missing", PartitionKey = "org" },
            new IdPartitionPair { Id = "one", PartitionKey = "org" }], cancellationToken: cancellationToken);
        Check(result.Count == 2 && result.All(d => d.Name == "found"), "Bulk results lost ordering, duplicates, or missing-item behavior.");
        ICosmosLibrarianDatabaseFactory factory = fixture.Resolve<ICosmosLibrarianDatabaseFactory>();
        Check(ReferenceEquals(await factory.Open("items", cancellationToken: cancellationToken), await factory.Open("items", cancellationToken: cancellationToken)), "Default handles were not reused.");
        Check(ReferenceEquals(await factory.Open("items", cancellationToken: cancellationToken), await factory.Open("items", new CosmosReadOptions(), cancellationToken: cancellationToken)), "Explicit SDK defaults allocated an unnecessary options view.");
        fixture.Containers.Verify(c => c.Get("items", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Store.Verify(c => c.ReadManyItemsAsync<NativeDocument>(It.IsAny<IReadOnlyList<(string, PartitionKey)>>(), It.IsAny<ReadManyRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Partition_reads_route_to_one_partition_and_id_queries_are_bounded(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        fixture.Store.Setup(c => c.GetItemLinqQueryable<NativeDocument>(false, It.IsAny<string>(), It.IsAny<QueryRequestOptions>(), It.IsAny<CosmosLinqSerializerOptions>()))
            .Returns(fixture.Native.GetItemLinqQueryable<NativeDocument>());
        var options = new List<QueryRequestOptions>();
        fixture.Store.Setup(c => c.GetItemQueryIterator<NativeDocument>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition _, string _, QueryRequestOptions request) => { options.Add(request); return Page<NativeDocument>([]); });
        CosmosAdapterRepository repository = fixture.Resolve<CosmosAdapterRepository>();
        await repository.GetLatestByPartitionKey("org", new CosmosReadOptions { SessionToken = "session" }, cancellationToken);
        Check(options[0].PartitionKey == new PartitionKey("org") && options[0].MaxItemCount == 1 && options[0].SessionToken == "session", "Partition read options were lost.");
        await repository.GetAllByDocumentIds([], cancellationToken: cancellationToken);
        Check(options.Count == 1, "Empty IDs issued a query.");
        await repository.GetAllByDocumentIds(Enumerable.Range(0, 101).Select(i => i.ToString()).ToList(), cancellationToken: cancellationToken);
        Check(options.Count == 4, "IDs were not queried in bounded batches.");
    }

    [Test]
    public async Task Paged_deletes_finish_each_page_before_reading_the_next(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        var deleted = new List<string>();
        fixture.Store.Setup(c => c.DeleteItemStreamAsync(It.IsAny<string>(), new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, PartitionKey _, ItemRequestOptions _, CancellationToken _) => { deleted.Add(id); return Response(HttpStatusCode.NoContent); });
        fixture.Store.Setup(c => c.GetItemQueryIterator<NativeDocument>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition _, string token, QueryRequestOptions options) =>
            {
                Check(options.MaxItemCount == 1, "Paged deletion ignored its page size.");
                Check(token is null, "Drain unexpectedly rebuilt the iterator.");
                int pageIndex = 0;
                var iterator = new Mock<FeedIterator<NativeDocument>>();
                iterator.SetupGet(i => i.HasMoreResults).Returns(() => pageIndex < 2);
                iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                {
                    if (pageIndex == 1) Check(deleted.SequenceEqual(["one"]), "Next page was fetched before its predecessor was consumed.");
                    NativeDocument[] rows = [new() { DocumentId = pageIndex++ == 0 ? "one" : "two", PartitionKey = "org" }];
                    var response = new Mock<FeedResponse<NativeDocument>>();
                    response.SetupGet(r => r.Count).Returns(rows.Length);
                    response.Setup(r => r.GetEnumerator()).Returns(() => ((IEnumerable<NativeDocument>)rows).GetEnumerator());
                    return response.Object;
                });
                return iterator.Object;
            });
        await fixture.Resolve<CosmosAdapterRepository>().DeleteItemsPaged(new QueryDefinition("SELECT * FROM c"), 1, cancellationToken: cancellationToken);
        Check(deleted.SequenceEqual(["one", "two"]), "Paged deletion lost identities.");
        fixture.Store.Verify(c => c.GetItemQueryIterator<NativeDocument>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Once);
    }

    [Test]
    public async Task Explicit_query_options_survive_paging_without_mutating_the_caller(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        var source = new QueryRequestOptions { PartitionKey = new PartitionKey("org"), MaxItemCount = 17, MaxConcurrency = 3,
            PopulateIndexMetrics = true, EnableOptimisticDirectExecution = true, SessionToken = "explicit" };
        fixture.Store.Setup(c => c.GetItemQueryIterator<int>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition _, string _, QueryRequestOptions options) =>
            {
                Check(options.MaxItemCount == 2 && options.MaxConcurrency == 3 && options.PopulateIndexMetrics == true &&
                    options.EnableOptimisticDirectExecution == true && options.SessionToken == "explicit" && options.PartitionKey == new PartitionKey("org"), "Query options were dropped.");
                return Page<int>([1]);
            });
        ICosmosLibrarianContainer container = await fixture.Resolve<ICosmosLibrarianDatabaseFactory>().Open("items",
            new CosmosReadOptions { SessionToken = "fallback" }, source, cancellationToken);
        source.MaxConcurrency = 99;
        await container.QueryPage<int>(new QueryDefinition("SELECT VALUE 1 FROM c"), 2, cancellationToken: cancellationToken);
        Check(source.MaxItemCount == 17, "Paging mutated caller options.");
    }

    [Test]
    public async Task Scalar_queries_skip_empty_pages_and_existence_stops_at_first_result(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        bool existence = false;
        int reads = 0;
        fixture.Store.Setup(c => c.GetItemQueryStreamIterator(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition _, string _, QueryRequestOptions options) =>
            {
                reads = 0;
                if (existence) Check(options.MaxItemCount == 1, "Existence did not constrain the page size.");
                var iterator = new Mock<FeedIterator>();
                iterator.SetupGet(i => i.HasMoreResults).Returns(() => reads < 3);
                iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                    Response(HttpStatusCode.OK, ++reads == 2 ? "{\"Documents\":[7]}" : "{\"Documents\":[]}"));
                return iterator.Object;
            });
        var store = await fixture.Resolve<ICosmosLibrarianDatabaseFactory>().Open("items", cancellationToken: cancellationToken);
        Check(await store.CountItems(cancellationToken) == 7 && reads == 3, "Count mishandled empty pages.");
        existence = true;
        Check(await store.ExistsByIndex("score_value", 7, cancellationToken) && reads == 2, "Existence read past its first match.");
    }

    [Test]
    public async Task Scoped_mutation_reads_once_per_attempt_and_revalidates_scope(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        int reads = 0, writes = 0;
        fixture.Store.Setup(c => c.ReadItemStreamAsync("one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.OK, "{\"id\":\"one\",\"partitionKey\":\"org\",\"name\":\"allowed\",\"score_value\":" + ++reads + "}", "read-" + reads));
        fixture.Store.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), "one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string _, PartitionKey _, ItemRequestOptions options, CancellationToken _) =>
            {
                Check(options.IfMatchEtag == "read-" + reads, "Scoped mutation lost its precondition.");
                return Response(++writes == 1 ? HttpStatusCode.PreconditionFailed : HttpStatusCode.OK, etag: "updated");
            });
        var repository = fixture.Resolve<ScopedCosmosAdapterRepository>();
        NativeDocument? result = await repository.MutateItem("org:one", row => { row.Score += 10; return true; }, cancellationToken: cancellationToken);
        Check(reads == 2 && writes == 2 && result?.Score == 12, "Scoped mutation reread unnecessarily or failed to replay a conflict.");
        bool rejected = false;
        try { await repository.MutateItem("org:one", row => { row.Name = "other"; return true; }, cancellationToken: cancellationToken); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && writes == 2, "Mutation escaped its scope.");
    }

    [Test]
    public async Task Linq_page_drain_reuses_iterator_and_disposes_on_callback_failure(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        fixture.Store.Setup(c => c.GetItemLinqQueryable<NativeDocument>(false, It.IsAny<string>(), It.IsAny<QueryRequestOptions>(), It.IsAny<CosmosLinqSerializerOptions>()))
            .Returns(fixture.Native.GetItemLinqQueryable<NativeDocument>());
        int reads = 0, callbacks = 0;
        var iterator = new Mock<FeedIterator<int>>();
        iterator.SetupGet(i => i.HasMoreResults).Returns(true);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            int[] rows = ++reads == 1 ? [] : [reads];
            var page = new Mock<FeedResponse<int>>();
            page.SetupGet(p => p.Count).Returns(rows.Length);
            page.Setup(p => p.GetEnumerator()).Returns(() => ((IEnumerable<int>)rows).GetEnumerator());
            return page.Object;
        });
        fixture.Store.Setup(c => c.GetItemQueryIterator<int>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions options) =>
            {
                Check(options.MaxItemCount == 7 && query.QueryText.Contains("score_value"), "LINQ projection or page size was lost.");
                return iterator.Object;
            });
        var repository = fixture.Resolve<CosmosAdapterRepository>();
        IQueryable<int> query = (await repository.BuildPagedQueryable(7, cancellationToken: cancellationToken)).Select(row => row.Score);
        var retained = new List<List<int>>();
        bool failed = false;
        try
        {
            await repository.ExecuteOnGetItemsPaged(query, page =>
            {
                retained.Add(page);
                if (++callbacks == 3) throw new InvalidOperationException("consumer failed");
                return ValueTask.CompletedTask;
            }, cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception.Message == "consumer failed") { failed = true; }
        Check(failed && reads == 3 && retained[0].Count == 0 && retained[1].SequenceEqual([2]), "Empty pages, callback failure, or list ownership changed.");
        fixture.Store.Verify(c => c.GetItemQueryIterator<int>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()), Times.Once);
        iterator.Protected().Verify("Dispose", Times.Once(), (object)true);
    }

    [Test]
    public async Task Mutation_rereads_after_conflict_and_returns_the_new_version(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        int reads = 0, writes = 0;
        fixture.Store.Setup(c => c.ReadItemStreamAsync("one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.OK, "{\"id\":\"one\",\"partitionKey\":\"org\",\"score_value\":" + ++reads + "}", "read-" + reads));
        fixture.Store.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), "one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string _, PartitionKey _, ItemRequestOptions options, CancellationToken _) =>
            {
                Check(options.IfMatchEtag == "read-" + reads, "Mutation reused a stale version.");
                return Response(++writes == 1 ? HttpStatusCode.PreconditionFailed : HttpStatusCode.OK, etag: "updated");
            });
        NativeDocument? result = await fixture.Resolve<CosmosAdapterRepository>().MutateItem("org:one", row => { row.Score += 10; return true; }, cancellationToken: cancellationToken);
        Check(reads == 2 && writes == 2 && result?.Score == 12, "Mutation did not replay against the latest document.");
    }
}
