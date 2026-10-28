using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Cosmos;

/// <summary>Native Cosmos operations in addition to the portable container contract.</summary>
public interface ICosmosLibrarianContainer : ILibrarianContainer
{
    /// <summary>Applies a native atomic patch and returns the updated JSON and version, or null on absence or a
    /// failed version/filter condition. Identity and provider-metadata paths cannot be patched.</summary>
    /// <remarks>ExpectedVersion is optional and enforced with a native filter predicate; it cannot be combined with a
    /// custom filter predicate from the options factory. Move operations are unsupported. This operation bypasses repository lifecycle hooks.</remarks>
    ValueTask<LibrarianItem<string>?> PatchItem(string id, IReadOnlyList<PatchOperation> operations,
        string? expectedVersion = null, CancellationToken cancellationToken = default);
}
