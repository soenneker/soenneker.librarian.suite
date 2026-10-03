using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Soenneker.Cosmos.Client.Abstract;
using Soenneker.Cosmos.Container.Abstract;
using Soenneker.Cosmos.Container.Registrars;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class CosmosPersistenceFixture : IAsyncDisposable
{
    private readonly CosmosClient _client;
    private readonly ServiceProvider _services;
    private readonly string _name;
    internal CosmosLibrarianDatabase Database { get; }
    internal CosmosLibrarianDatabase CreateDatabase() => new(_services.GetRequiredService<ICosmosContainerUtil>(), "test");
    private CosmosPersistenceFixture(CosmosClient client, ServiceProvider services, string name)
    {
        _client = client;
        _services = services;
        _name = name;
        Database = CreateDatabase();
    }
    internal static Task<CosmosPersistenceFixture> Create()
    {
        string? connection = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_COSMOS");
        if (string.IsNullOrWhiteSpace(connection)) Skip.Test("Set LIBRARIAN_TEST_COSMOS to run Cosmos integration tests (creates and deletes an isolated test database).");
        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway, RequestTimeout = TimeSpan.FromSeconds(15),
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        };
        if (Environment.GetEnvironmentVariable("LIBRARIAN_TEST_COSMOS_EMULATOR") == "1")
        {
            if (!connection!.Contains("AccountEndpoint=https://localhost:", StringComparison.OrdinalIgnoreCase) &&
                !connection.Contains("AccountEndpoint=https://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Emulator certificate bypass is restricted to localhost.");
            options.HttpClientFactory = () => new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        }
        var client = new CosmosClient(connection, options);
        string name = "librarian-test-" + Guid.NewGuid().ToString("N");
        var parsed = new DbConnectionStringBuilder { ConnectionString = connection! };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:Cosmos:Endpoint"] = (string)parsed["AccountEndpoint"], ["Azure:Cosmos:AccountKey"] = (string)parsed["AccountKey"],
            ["Azure:Cosmos:DatabaseName"] = name, ["Azure:Cosmos:DatabaseThroughput"] = "400",
            ["Azure:Cosmos:DatabaseThroughputType"] = "manual"
        }).Build();
        var clients = new Mock<ICosmosClientUtil>();
        clients.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(client);
        ServiceProvider services = new ServiceCollection().AddLogging().AddSingleton(configuration).AddSingleton(clients.Object)
            .AddCosmosContainerUtilAsSingleton().BuildServiceProvider();
        return Task.FromResult(new CosmosPersistenceFixture(client, services, name));
    }
    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        try
        {
            try { await _client.GetDatabase(_name).DeleteAsync(); }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { }
        }
        finally { await _services.DisposeAsync(); _client.Dispose(); }
    }
}
