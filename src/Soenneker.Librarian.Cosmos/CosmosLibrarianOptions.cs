using System;
using Microsoft.Azure.Cosmos;

namespace Soenneker.Librarian.Cosmos;

/// <summary>Provider controls fixed for the lifetime of a Cosmos database instance.</summary>
/// <remarks>Factories must return fresh request options and be thread-safe. Librarian always enforces the selected
/// partition and explicit version conditions after invoking factories. Session tokens can be supplied by factories
/// and captured by Observer. These controls do not change client-level retry or connection settings.</remarks>
public sealed class CosmosLibrarianOptions
{
    /// <summary>Maps a logical container name to a physical ID when using the Cosmos container utilities.</summary>
    public Func<string, string>? ContainerNameResolver { get; init; }
    /// <summary>Creates options for a named container and operation (AddItem, GetItem, UpdateItem, DeleteItem, UpdateItemIfVersion, or DeleteItemIfVersion).</summary>
    public Func<string, string, ItemRequestOptions>? ItemRequestOptionsFactory { get; init; }
    /// <summary>Creates query options, including consistency, session tokens, buffering, and diagnostics settings.</summary>
    public Func<string, QueryRequestOptions>? QueryRequestOptionsFactory { get; init; }
    /// <summary>Creates options for native bulk reads.</summary>
    public Func<string, ReadManyRequestOptions>? ReadManyRequestOptionsFactory { get; init; }
    /// <summary>Creates options for native patch operations.</summary>
    public Func<string, PatchItemRequestOptions>? PatchRequestOptionsFactory { get; init; }
    /// <summary>Creates options for partition-local transactions.</summary>
    public Func<string, TransactionalBatchRequestOptions>? BatchRequestOptionsFactory { get; init; }
    /// <summary>Receives response metadata. Observer failures are traced and never change the storage outcome.</summary>
    public ICosmosLibrarianObserver? Observer { get; init; }
}
