using System;
using Soenneker.Atomics.ValueBools;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Cosmos.Container.Abstract;
using Soenneker.Cosmos.Repository.Dtos;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Cosmos;

public sealed class CosmosLibrarianDatabaseFactory(ICosmosContainerUtil containers) : ICosmosLibrarianDatabaseFactory
{
    private ValueAtomicBool _disposed = new(false);
    private readonly Lazy<CosmosLibrarianDatabase> _defaultDatabase = new(() =>
        new CosmosLibrarianDatabase((container, token) => containers.Get(container, token), "repositories"));

    public async ValueTask<ICosmosLibrarianContainer> Open(string name, CosmosReadOptions? readOptions = null,
        QueryRequestOptions? queryOptions = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (queryOptions is null && readOptions?.ReadConsistencyStrategy is null && readOptions?.SessionToken is null)
            return await _defaultDatabase.Value.GetCosmosContainer(name, cancellationToken: cancellationToken);
        QueryRequestOptions? snapshot = queryOptions is null ? null : CosmosQueryOptions.Copy(queryOptions);
        string? partition = Partition(snapshot?.PartitionKey);
        var container = (CosmosLibrarianContainer)await _defaultDatabase.Value.GetCosmosContainer(name, cancellationToken: cancellationToken);
        return container.WithOptions(new CosmosLibrarianOptions
            {
                ItemRequestOptionsFactory = (_, _) => new ItemRequestOptions { ReadConsistencyStrategy = readOptions?.ReadConsistencyStrategy, SessionToken = readOptions?.SessionToken },
                QueryRequestOptionsFactory = _ => snapshot is not null ? CosmosQueryOptions.Copy(snapshot) : new QueryRequestOptions
                { ReadConsistencyStrategy = readOptions?.ReadConsistencyStrategy, SessionToken = readOptions?.SessionToken },
                ReadManyRequestOptionsFactory = _ => new ReadManyRequestOptions { ReadConsistencyStrategy = readOptions?.ReadConsistencyStrategy, SessionToken = readOptions?.SessionToken }
            }, partition);
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
        if (!_disposed.TrySetTrue()) return;
        // Force lazy initialization so a racing first Open cannot create an undisposed owner.
        await _defaultDatabase.Value.DisposeAsync();
    }
}
