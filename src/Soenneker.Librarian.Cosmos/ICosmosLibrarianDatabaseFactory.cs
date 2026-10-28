using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Cosmos.Repository.Dtos;

namespace Soenneker.Librarian.Cosmos;

/// <summary>Owns scoped Librarian handles with isolated read options and existing physical container names.</summary>
/// <remarks>Register with a scoped lifetime. Handles and deferred queries remain valid until the factory is disposed.</remarks>
public interface ICosmosLibrarianDatabaseFactory : IAsyncDisposable
{
    /// <summary>Opens a container using its physical name and the supplied request options.</summary>
    /// <param name="name">The existing physical container name.</param>
    /// <param name="readOptions">Optional consistency and session settings for reads.</param>
    /// <param name="queryOptions">Optional query settings, including a single string partition key.</param>
    /// <param name="cancellationToken">Cancels opening the container.</param>
    /// <returns>A handle owned by this factory's scope.</returns>
    ValueTask<ICosmosLibrarianContainer> Open(string name, CosmosReadOptions? readOptions = null,
        QueryRequestOptions? queryOptions = null, CancellationToken cancellationToken = default);
}
