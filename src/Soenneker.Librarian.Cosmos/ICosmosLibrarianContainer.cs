using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Cosmos;

/// <summary>Native Cosmos operations in addition to the portable container contract.</summary>
public interface ICosmosLibrarianContainer : ILibrarianContainer
{
    /// <summary>Checks a document's existence using a point read without deserializing its body. Only not-found returns false.</summary>
    ValueTask<bool> Exists(string id, CancellationToken cancellationToken = default);

    /// <summary>Serializes a document with the container client's configured serializer, excluding Cosmos metadata.</summary>
    string SerializeDocument<T>(T document);

    /// <summary>Deserializes a document with the container client's configured serializer and optional response ETag.</summary>
    T DeserializeDocument<T>(string json, string? version = null);

    /// <summary>Drains a native SQL query using one iterator, optionally delaying after each nonempty page.</summary>
    ValueTask<List<T>> QueryItems<T>(QueryDefinition query, double? delayMs = null, CancellationToken cancellationToken = default);

    /// <summary>Executes a native SQL query page within this handle's partition scope. SQL and projections are caller-owned.
    /// The continuation token is the native Cosmos token and must be reused only with the same query and scope.</summary>
    ValueTask<Soenneker.Librarian.Abstractions.Queries.LibrarianPage<T>> QueryPage<T>(QueryDefinition query, int pageSize = 100,
        string? continuationToken = null, CancellationToken cancellationToken = default);
    /// <summary>Applies a native atomic patch and returns the updated JSON and version, or null on absence or a
    /// failed version/filter condition. Identity and provider-metadata paths cannot be patched.</summary>
    /// <remarks>ExpectedVersion is optional and enforced with a native filter predicate; it cannot be combined with a
    /// custom filter predicate from the options factory. Move operations are unsupported. This operation bypasses repository lifecycle hooks.</remarks>
    ValueTask<LibrarianItem<string>?> PatchItem(string id, IReadOnlyList<PatchOperation> operations,
        string? expectedVersion = null, CancellationToken cancellationToken = default);
}
