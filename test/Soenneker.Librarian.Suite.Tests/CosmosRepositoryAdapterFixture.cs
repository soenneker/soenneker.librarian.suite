using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Soenneker.Cosmos.Container.Abstract;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Suite.Tests;

// Explicit adapter-boundary fixture: the container resolver and SDK are the boundaries under test.
// The repository and database factory are real services, resolved from the provider.
internal sealed class CosmosRepositoryAdapterFixture : IAsyncDisposable
{
    private readonly CosmosClient _client = new("https://localhost:8081/", Convert.ToBase64String(new byte[64]),
        new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) });
    private readonly ServiceProvider _services;
    internal Mock<Container> Store { get; } = new(MockBehavior.Strict);
    internal Mock<ICosmosContainerUtil> Containers { get; } = new(MockBehavior.Strict);
    internal Container Native => _client.GetContainer("test", "items");

    internal CosmosRepositoryAdapterFixture()
    {
        Store.SetupGet(c => c.Database).Returns(Native.Database);
        Store.SetupGet(c => c.Id).Returns("items");
        Containers.Setup(c => c.Get("items", It.IsAny<CancellationToken>())).ReturnsAsync(Store.Object);
        _services = new ServiceCollection().AddSingleton(Containers.Object)
            .AddSingleton<ICosmosLibrarianDatabaseFactory, CosmosLibrarianDatabaseFactory>()
            .AddSingleton<CosmosAdapterRepository>().AddSingleton<ScopedCosmosAdapterRepository>().BuildServiceProvider();
    }

    internal T Resolve<T>() where T : notnull => _services.GetRequiredService<T>();
    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _client.Dispose();
    }
}
