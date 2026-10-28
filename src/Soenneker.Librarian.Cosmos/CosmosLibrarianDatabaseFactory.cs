using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Cosmos.Container.Abstract;
using Soenneker.Cosmos.Repository.Dtos;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Cosmos;

public sealed class CosmosLibrarianDatabaseFactory(ICosmosContainerUtil containers) : ICosmosLibrarianDatabaseFactory
{
    private readonly ConcurrentBag<CosmosLibrarianDatabase> _databases = [];
    private readonly Lazy<CosmosLibrarianDatabase> _defaultDatabase = new(() =>
        new CosmosLibrarianDatabase((container, token) => containers.Get(container, token), "repositories"));

    public async ValueTask<ICosmosLibrarianContainer> Open(string name, CosmosReadOptions? readOptions = null,
        QueryRequestOptions? queryOptions = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (readOptions is null && queryOptions is null)
            return await _defaultDatabase.Value.GetCosmosContainer(name, cancellationToken: cancellationToken);
        QueryRequestOptions? snapshot = queryOptions is null ? null : CosmosQueryOptions.Copy(queryOptions);
        var database = new CosmosLibrarianDatabase((container, token) => containers.Get(container, token), "repositories",
            new CosmosLibrarianOptions
            {
                ItemRequestOptionsFactory = (_, _) => new ItemRequestOptions { ReadConsistencyStrategy = readOptions?.ReadConsistencyStrategy, SessionToken = readOptions?.SessionToken },
                QueryRequestOptionsFactory = _ => snapshot is not null ? CosmosQueryOptions.Copy(snapshot) : new QueryRequestOptions
                { ReadConsistencyStrategy = readOptions?.ReadConsistencyStrategy, SessionToken = readOptions?.SessionToken },
                ReadManyRequestOptionsFactory = _ => new ReadManyRequestOptions { ReadConsistencyStrategy = readOptions?.ReadConsistencyStrategy, SessionToken = readOptions?.SessionToken }
            });
        _databases.Add(database);
        // Queries may outlive their construction call, so options and handles live until the DI scope ends.
        return await database.GetCosmosContainer(name, Partition(snapshot?.PartitionKey), cancellationToken);
    }

    private static string? Partition(PartitionKey? partition)
    {
        if (partition is null) return null;
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(partition.Value.ToString());
        if (json.RootElement.GetArrayLength() != 1 || json.RootElement[0].ValueKind != System.Text.Json.JsonValueKind.String)
            throw new NotSupportedException("Repository containers require single string partition keys.");
        return json.RootElement[0].GetString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_defaultDatabase.IsValueCreated) await _defaultDatabase.Value.DisposeAsync();
        foreach (CosmosLibrarianDatabase database in _databases) await database.DisposeAsync();
    }
}
