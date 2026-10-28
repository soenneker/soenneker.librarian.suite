using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Moq;
using Moq.Protected;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

// These tests exercise the Cosmos SDK adapter boundary, using the real SDK LINQ translator without network access.
public sealed class CosmosExecutionTests
{
    private static CosmosClient Client() => new("https://localhost:8081/", Convert.ToBase64String(new byte[64]),
        new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) });

    private static Mock<Container> Store(CosmosClient client)
    {
        Container native = client.GetContainer("test", "items");
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        store.SetupGet(c => c.Database).Returns(native.Database);
        store.Setup(c => c.GetItemLinqQueryable<NativeDocument>(false, It.IsAny<string>(), It.IsAny<QueryRequestOptions>(), It.IsAny<CosmosLinqSerializerOptions>()))
            .Returns(native.GetItemLinqQueryable<NativeDocument>());
        return store;
    }

    private static FeedIterator<T> Pages<T>(params T[][] pages)
    {
        int index = 0;
        var iterator = new Mock<FeedIterator<T>>(MockBehavior.Strict);
        iterator.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        iterator.SetupGet(i => i.HasMoreResults).Returns(() => index < pages.Length);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            T[] values = pages[index++];
            var response = new Mock<FeedResponse<T>>();
            response.SetupGet(r => r.Count).Returns(values.Length);
            response.SetupGet(r => r.Resource).Returns(values);
            response.Setup(r => r.GetEnumerator()).Returns(() => ((IEnumerable<T>)values).GetEnumerator());
            return response.Object;
        });
        return iterator.Object;
    }

    [Test]
    public async Task Any_and_All_project_scalars_and_rewrite_nulls_after_composition(CancellationToken cancellationToken)
    {
        using CosmosClient client = Client();
        Mock<Container> store = Store(client);
        var queries = new List<string>();
        store.Setup(c => c.GetItemQueryIterator<int>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions options) =>
            {
                queries.Add(query.QueryText);
                Check(options.MaxItemCount == 1 && options.PartitionKey == new PartitionKey("org"), "Existence query lost its limit or partition.");
                return Pages<int>([], [1]);
            });
        await using var database = new CosmosLibrarianDatabase(store.Object);
        IQueryable<NativeDocument> query = (await database.GetCosmosContainer("items", "org", cancellationToken)).BuildQueryable<NativeDocument>();
        Check(await query.Any(row => row.Name == null, cancellationToken), "Any stopped at an empty page.");
        Check(!await query.All(row => row.Name != null, cancellationToken), "All missed the counterexample.");
        Check(queries.All(sql => sql.Contains("IS_DEFINED") && sql.Contains("VALUE 1")), "Existence queries materialize documents or omit null rewriting.");
    }

    [Test]
    public async Task First_and_Single_use_bounded_pages_and_detect_multiple_results(CancellationToken cancellationToken)
    {
        using CosmosClient client = Client();
        Mock<Container> store = Store(client);
        store.Setup(c => c.GetItemQueryIterator<NativeDocument>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions options) =>
            {
                Check(query.QueryText.Contains("IS_DEFINED"), "Null rewriting was skipped.");
                Check(options.MaxItemCount is 1 or 2, "Terminal request is not bounded.");
                return options.MaxItemCount == 1 ? Pages<NativeDocument>([], [new() { Name = "first" }])
                    : Pages<NativeDocument>([new()], [], [new()]);
            });
        await using var database = new CosmosLibrarianDatabase(store.Object);
        IQueryable<NativeDocument> query = (await database.GetCosmosContainer("items", cancellationToken: cancellationToken)).BuildQueryable<NativeDocument>().Where(row => row.Name == null);
        Check((await query.First(cancellationToken)).Name == "first", "First stopped at an empty page.");
        bool rejected = false;
        try { await query.Single(cancellationToken); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Single accepted two rows across pages.");
    }

    [Test]
    public async Task Count_lists_and_continuation_pages_rewrite_nulls(CancellationToken cancellationToken)
    {
        using CosmosClient client = Client();
        Mock<Container> store = Store(client);
        var queries = new List<string>();
        store.Setup(c => c.GetItemQueryIterator<int>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions _) => { queries.Add(query.QueryText); return Pages<int>([3]); });
        store.Setup(c => c.GetItemQueryIterator<NativeDocument>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions _) => { queries.Add(query.QueryText); return Pages<NativeDocument>(new NativeDocument[] { new() }); });
        await using var database = new CosmosLibrarianDatabase(store.Object);
        IQueryable<NativeDocument> query = (await database.GetCosmosContainer("items", cancellationToken: cancellationToken)).BuildQueryable<NativeDocument>().Where(row => row.Name == null);
        Check(await query.Count(cancellationToken) == 3, "Count failed.");
        Check((await query.ToList(cancellationToken)).Count == 1, "List failed.");
        Check((await query.ToPage(5, cancellationToken: cancellationToken)).Items.Count == 1, "Page failed.");
        Check(queries.Count == 3 && queries.All(sql => sql.Contains("IS_DEFINED")), "An execution path skipped null rewriting.");
    }

    [Test]
    public async Task Point_existence_never_parses_content_and_only_not_found_is_false(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        store.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, PartitionKey _, ItemRequestOptions _, CancellationToken _) =>
                new ResponseMessage(id == "missing" ? HttpStatusCode.NotFound : id == "forbidden" ? HttpStatusCode.Forbidden : HttpStatusCode.OK));
        await using var database = new CosmosLibrarianDatabase(store.Object);
        ICosmosLibrarianContainer container = await database.GetCosmosContainer("items", "org", cancellationToken);
        Check(await container.Exists("present", cancellationToken), "Existence required a response body.");
        Check(!await container.Exists("missing", cancellationToken), "Missing document exists.");
        bool rejected = false;
        try { await container.Exists("forbidden", cancellationToken); }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Forbidden) { rejected = true; }
        Check(rejected, "Existence hid an authorization failure.");
    }

    [Test]
    public async Task Writes_suppress_unused_response_content_and_keep_conditional_versions(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        store.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), "one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string _, PartitionKey _, ItemRequestOptions options, CancellationToken _) =>
            {
                Check(options.EnableContentResponseOnWrite == false && options.IfMatchEtag == "old", "Conditional write options were lost.");
                var response = new ResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("etag", "new");
                return response;
            });
        await using var database = new CosmosLibrarianDatabase(store.Object);
        ICosmosLibrarianContainer container = await database.GetCosmosContainer("items", "org", cancellationToken);
        var result = await container.UpdateItemIfVersion("one", NativeDocumentJson.Create("one", "{}", "org"), "old", cancellationToken);
        Check(result?.Version == "new", "Response suppression lost the new ETag.");
    }
}
