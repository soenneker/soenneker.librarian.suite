using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Azure.Cosmos.Scripts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Soenneker.Cosmos.Client.Abstract;
using Soenneker.Cosmos.Container.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Cosmos;
using Soenneker.Librarian.Cosmos.Registrars;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosProviderTests
{
    private static ResponseMessage Response(HttpStatusCode status, string? json = null)
    {
        var response = new ResponseMessage(status);
        response.Headers.Add("etag", "etag");
        if (json is not null) response.Content = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return response;
    }

    [Test]
    public async Task First_access_ensures_database_and_container_through_existing_utilities()
    {
        var client = new Mock<CosmosClient>();
        var sdkDatabase = new Mock<Database>();
        sdkDatabase.SetupGet(d => d.Client).Returns(client.Object);
        sdkDatabase.SetupGet(d => d.Id).Returns("test-database");
        var container = new Mock<Container>();
        var databaseResponse = new Mock<DatabaseResponse>();
        databaseResponse.SetupGet(r => r.Database).Returns(sdkDatabase.Object);
        var containerResponse = new Mock<ContainerResponse>();
        containerResponse.SetupGet(r => r.Container).Returns(container.Object);
        client.Setup(c => c.CreateDatabaseIfNotExistsAsync("test-database", It.IsAny<ThroughputProperties>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(databaseResponse.Object);
        client.Setup(c => c.GetDatabase("test-database")).Returns(sdkDatabase.Object);
        client.Setup(c => c.GetContainer("test-database", "librarian")).Returns(container.Object);
        sdkDatabase.Setup(d => d.CreateContainerIfNotExistsAsync(It.IsAny<ContainerProperties>(), It.IsAny<ThroughputProperties>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<ContainerProperties, ThroughputProperties, RequestOptions, CancellationToken>((p, _, _, _) =>
                DocumentProviderAssertions.Check(p.Id == "librarian" && p.PartitionKeyPath == "/partitionKey", "Wrong container schema."))
            .ReturnsAsync(containerResponse.Object);
        var clients = new Mock<ICosmosClientUtil>();
        clients.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:Cosmos:Endpoint"] = "https://localhost:8081/", ["Azure:Cosmos:AccountKey"] = "test",
            ["Azure:Cosmos:DatabaseName"] = "test-database", ["Azure:Cosmos:DatabaseThroughput"] = "400",
            ["Azure:Cosmos:DatabaseThroughputType"] = "manual"
        }).Build();
        await using ServiceProvider services = new ServiceCollection().AddLogging().AddSingleton(configuration)
            .AddSingleton(clients.Object).AddCosmosLibrarianDatabaseAsSingleton().BuildServiceProvider();
        ILibrarianDatabase database = services.GetRequiredService<ILibrarianDatabase>();
        client.Verify(c => c.GetContainer(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        ILibrarianContainer first = await database.GetContainer("items");
        DocumentProviderAssertions.Check(ReferenceEquals(first, await database.GetContainer("items")), "Handle not cached.");
        await database.GetContainer("other");
        client.Verify(c => c.CreateDatabaseIfNotExistsAsync("test-database", It.IsAny<ThroughputProperties>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        sdkDatabase.Verify(d => d.CreateContainerIfNotExistsAsync(It.IsAny<ContainerProperties>(), It.IsAny<ThroughputProperties>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        await database.DisposeAsync();
        clients.Verify(c => c.Dispose(), Times.Never);
    }

    [Test]
    public async Task Failed_first_access_can_retry_and_cancellation_does_not_initialize()
    {
        var util = new Mock<ICosmosContainerUtil>(MockBehavior.Strict);
        var container = new Mock<Container>();
        util.SetupSequence(u => u.Get("librarian", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Setup failed")).ReturnsAsync(container.Object);
        await using var database = new CosmosLibrarianDatabase(util.Object, "librarian");
        try { await database.GetContainer("items", new CancellationToken(true)); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        util.Verify(u => u.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        try { await database.GetContainer("items"); throw new Exception("Setup error swallowed."); } catch (InvalidOperationException) { }
        await database.GetContainer("items");
        util.Verify(u => u.Get("librarian", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task Add_is_one_native_point_write_without_metadata_or_encoded_indexes()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        string? payload = null;
        container.Setup(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), new PartitionKey("key"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, PartitionKey, ItemRequestOptions, CancellationToken>((stream, _, _, _) => payload = new StreamReader(stream, leaveOpen: true).ReadToEnd())
            .ReturnsAsync(() => Response(HttpStatusCode.Created));
        await using var database = new CosmosLibrarianDatabase(container.Object, "key");
        ILibrarianContainer items = await database.GetContainer("items");
        await items.AddItem("ID/with?#", "{ \"amount\": 1.00 }");
        using JsonDocument json = JsonDocument.Parse(payload!);
        DocumentProviderAssertions.Check(json.RootElement.GetProperty("body").GetProperty("amount").GetDecimal() == 1m, "Document is not natively queryable.");
        DocumentProviderAssertions.Check(json.RootElement.GetProperty("rawJson").GetString() == "{ \"amount\": 1.00 }", "Raw JSON changed.");
        DocumentProviderAssertions.Check(!json.RootElement.TryGetProperty("values", out _), "Encoded indexes retained.");
        container.Verify(c => c.CreateItemStreamAsync(It.IsAny<Stream>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        container.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Reads_propagate_authorization_errors()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        container.Setup(c => c.ReadItemStreamAsync(It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.Forbidden));
        await using var database = new CosmosLibrarianDatabase(container.Object);
        try { await (await database.GetContainer("items")).GetItem("a"); throw new Exception("Authorization failure treated as missing."); }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Forbidden) { }
    }

    [Test]
    public async Task Batches_lazily_deploy_one_procedure_and_pass_raw_conditions()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var scripts = new Mock<Scripts>(MockBehavior.Strict);
        var result = new Mock<StoredProcedureExecuteResponse<bool>>();
        result.SetupGet(r => r.Resource).Returns(false);
        scripts.Setup(s => s.CreateStoredProcedureAsync(It.IsAny<StoredProcedureProperties>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<StoredProcedureResponse>());
        string? captured = null;
        scripts.Setup(s => s.ExecuteStoredProcedureAsync<bool>(CosmosBatchScript.Id, new PartitionKey("key"), It.IsAny<dynamic[]>(), It.IsAny<StoredProcedureRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, PartitionKey, dynamic[], StoredProcedureRequestOptions, CancellationToken>((_, _, args, _, _) => captured = (string)args[0])
            .ReturnsAsync(result.Object);
        container.SetupGet(c => c.Scripts).Returns(scripts.Object);
        await using var database = new CosmosLibrarianDatabase(container.Object, "key");
        var batch = new LibrarianBatch([new LibrarianWrite("other", "B", "new")], [new LibrarianCondition("items", "a", "before")]);
        DocumentProviderAssertions.Check(!await database.Execute(batch), "Failed condition reported success.");
        await database.Execute(batch);
        using JsonDocument payload = JsonDocument.Parse(captured!);
        DocumentProviderAssertions.Check(payload.RootElement.GetProperty("conditions")[0].GetProperty("expected").GetString() == "before", "Condition changed.");
        scripts.Verify(s => s.CreateStoredProcedureAsync(It.IsAny<StoredProcedureProperties>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Queries_use_native_sdk_translation_for_filters_projections_and_aggregates()
    {
        using var client = new CosmosClient("https://localhost:8081/", Convert.ToBase64String(new byte[64]), new CosmosClientOptions
        { UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) });
        Container container = client.GetContainer("test", "librarian");
        await using var database = new CosmosLibrarianDatabase(container);
        IQueryable<QueryableRow> query = (await database.GetContainer("items")).BuildQueryable<QueryableRow>();
        IQueryable<QueryableRow> filtered = query.Where(row => row.Score > 1 && row.Name!.Contains("hello")).OrderBy(row => row.Score).ThenBy(row => row.Name).Take(10);
        IQueryProvider native = container.GetItemLinqQueryable<QueryableRow>().Provider;
        string sql = native.CreateQuery<QueryableRow>(filtered.Expression).ToQueryDefinition().QueryText;
        DocumentProviderAssertions.Check(sql.Contains("score_value", StringComparison.Ordinal) && sql.Contains("CONTAINS", StringComparison.OrdinalIgnoreCase), "Native translation not used.");
        Expression<Func<IQueryable<QueryableRow>, int>> count = q => q.Count();
        var call = (MethodCallExpression)count.Body;
        string aggregate = native.CreateQuery<int>(call.Update(null, [query.Expression])).ToQueryDefinition().QueryText;
        DocumentProviderAssertions.Check(aggregate.Contains("COUNT", StringComparison.OrdinalIgnoreCase), "Native aggregate translation failed.");
    }
}
