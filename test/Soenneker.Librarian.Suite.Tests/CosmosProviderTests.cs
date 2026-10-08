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
using Soenneker.Cosmos.Container.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosProviderTests
{
    [Test]
    public async Task Raw_reads_strip_only_top_level_metadata_and_preserve_json_values(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        store.Setup(c => c.ReadItemStreamAsync("one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.OK, "{\"id\":\"one\",\"partitionKey\":\"org\",\"_etag\":\"remove\",\"nested\":{\"_etag\":\"keep\"},\"amount\":79228162514264337593543950335,\"text\":\"é😀\"}"));
        await using var database = new CosmosLibrarianDatabase(store.Object);
        string json = (await (await database.GetContainer("items", cancellationToken: cancellationToken)).GetItem("org:one", cancellationToken: cancellationToken))!;
        using JsonDocument parsed = JsonDocument.Parse(json);
        Check(!parsed.RootElement.TryGetProperty("_etag", out _) && parsed.RootElement.GetProperty("nested").GetProperty("_etag").GetString() == "keep" &&
            parsed.RootElement.GetProperty("amount").GetDecimal() == decimal.MaxValue && parsed.RootElement.GetProperty("text").GetString() == "é😀", "Native JSON conversion changed data.");
    }

    private static ResponseMessage Response(HttpStatusCode status, string? json = null)
    {
        var response = new ResponseMessage(status);
        response.Headers.Add("etag", "etag");
        if (json is not null) response.Content = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return response;
    }
    [Test]
    public async Task First_access_provisions_each_physical_container_once_through_utilities(CancellationToken cancellationToken)
    {
        var util = new Mock<ICosmosContainerUtil>(MockBehavior.Strict);
        util.Setup(u => u.Get(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<Container>());
        await using var database = new CosmosLibrarianDatabase(util.Object, "key");
        util.Verify(u => u.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        ILibrarianContainer first = await database.GetContainer("items", cancellationToken: cancellationToken);
        Check(ReferenceEquals(first, await database.GetContainer("items", cancellationToken: cancellationToken)), "Handle not cached.");
        await database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
        await database.GetContainer("other", cancellationToken: cancellationToken);
        util.Verify(u => u.Get("librarian.key.items", It.IsAny<CancellationToken>()), Times.Once);
        util.Verify(u => u.Get("librarian.key.other", It.IsAny<CancellationToken>()), Times.Once);
    }
    [Test]
    public async Task Failed_first_access_retries_and_cancellation_does_not_initialize(CancellationToken cancellationToken)
    {
        var util = new Mock<ICosmosContainerUtil>(MockBehavior.Strict);
        util.SetupSequence(u => u.Get("librarian.key.items", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Setup failed")).ReturnsAsync(Mock.Of<Container>());
        await using var database = new CosmosLibrarianDatabase(util.Object, "key");
        try { await database.GetContainer("items", new CancellationToken(true)); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        util.Verify(u => u.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        try { await database.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Failure hidden."); } catch (InvalidOperationException) { }
        await database.GetContainer("items", cancellationToken: cancellationToken);
        util.Verify(u => u.Get("librarian.key.items", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
    [Test]
    public async Task Document_json_is_written_directly_using_its_partition_key(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        string? payload = null;
        store.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), new PartitionKey("org-a"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, PartitionKey, ItemRequestOptions, CancellationToken>((stream, _, _, _) => payload = new StreamReader(stream, leaveOpen: true).ReadToEnd())
            .ReturnsAsync(() => Response(HttpStatusCode.Created));
        await using var database = new CosmosLibrarianDatabase(store.Object);
        ILibrarianContainer items = await database.GetContainer("items", cancellationToken: cancellationToken);
        string document = NativeDocumentJson.Create("org-a:one", "{\"score_value\":7}");
        await items.AddItem("org-a:one", document, cancellationToken: cancellationToken);
        Check(payload == document && !payload.Contains("rawJson") && !payload.Contains("body"), "Document was wrapped or transformed.");
        try { await items.AddItem("wrong", document, cancellationToken: cancellationToken); throw new Exception("Identity mismatch accepted."); } catch (ArgumentException) { }
        store.Verify(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Test]
    public async Task Reads_use_document_identity_and_propagate_authorization_errors(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        store.Setup(c => c.ReadItemStreamAsync("one", new PartitionKey("org-a"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Response(HttpStatusCode.Forbidden));
        await using var database = new CosmosLibrarianDatabase(store.Object);
        try { await (await database.GetContainer("items", cancellationToken: cancellationToken)).GetItem("org-a:one", cancellationToken: cancellationToken); throw new Exception("Authorization error hidden."); }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Forbidden) { }
        try { await database.GetContainer("other", cancellationToken: cancellationToken); throw new Exception("Caller-owned container leaked into another name."); } catch (ArgumentException) { }
    }
    [Test]
    public async Task Batches_use_native_etag_operations_without_reading_documents(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        store.SetupGet(c => c.Id).Returns("items");
        var transaction = new Mock<TransactionalBatch>(MockBehavior.Strict);
        var response = new Mock<TransactionalBatchResponse>();
        response.SetupGet(r => r.IsSuccessStatusCode).Returns(true);
        store.Setup(c => c.CreateTransactionalBatch(new PartitionKey("org-a"))).Returns(transaction.Object);
        transaction.Setup(t => t.ReplaceItemStream("one", It.IsAny<Stream>(), It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "version"))).Returns(transaction.Object);
        transaction.Setup(t => t.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>())).Returns(transaction.Object);
        transaction.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
        await using var database = new CosmosLibrarianDatabase(store.Object);
        var batch = new LibrarianBatch([
            new LibrarianWrite("items", "org-a:one", NativeDocumentJson.Create("org-a:one"), "version"),
            new LibrarianWrite("items", "org-a:audit", NativeDocumentJson.Create("org-a:audit"), CreateOnly: true)]);
        Check(await database.Execute(batch, cancellationToken: cancellationToken), "Native batch failed.");
        transaction.Verify(t => t.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        foreach (LibrarianBatch invalid in new[] {
            new LibrarianBatch([new LibrarianWrite("items", "org-a:one", null), new LibrarianWrite("other", "org-a:two", null)]),
            new LibrarianBatch([new LibrarianWrite("items", "org-a:one", null), new LibrarianWrite("items", "org-b:two", null)]),
            new LibrarianBatch([], [new LibrarianCondition("items", "org-a:one", null)]) })
        {
            try { await database.Execute(invalid, cancellationToken: cancellationToken); throw new Exception("Unsupported batch accepted."); } catch (NotSupportedException) { }
        }
    }
    [Test]
    public async Task Native_linq_translates_document_members_without_a_body_projection(CancellationToken cancellationToken)
    {
        using var client = new CosmosClient("https://localhost:8081/", Convert.ToBase64String(new byte[64]), new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) });
        Container store = client.GetContainer("test", "items");
        await using var database = new CosmosLibrarianDatabase(store);
        IQueryable<int> query = (await database.GetContainer("items", cancellationToken: cancellationToken)).BuildQueryable<NativeDocument>()
                                                                      .Where(row => row.PartitionKey == "org-a" && row.DocumentId == "one" && row.Score > 1 && row.Name!.Contains("hello")).Select(row => row.Score);
        string sql = store.GetItemLinqQueryable<NativeDocument>().Provider.CreateQuery<int>(query.Expression).ToQueryDefinition().QueryText;
        Check(sql.Contains("score_value") && sql.Contains("partitionKey") && sql.Contains("CONTAINS") && !sql.Contains("body"), "Direct native translation failed.");
    }
}
