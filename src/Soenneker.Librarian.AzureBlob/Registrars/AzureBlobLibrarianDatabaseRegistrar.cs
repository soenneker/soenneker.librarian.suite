using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.AzureBlob.Registrars;

/// <summary>Registers the single-owner Azure Blob Storage snapshot provider.</summary>
public static class AzureBlobLibrarianDatabaseRegistrar
{
    /// <summary>Registers an Azure Blob database as a singleton.</summary>
    /// <remarks>Requires Librarian:AzureBlob:ConnectionString and ContainerName. BlobName defaults to librarian.json.
    /// The container must exist. A factory can supply a BlobClient using managed identity or SAS authentication.</remarks>
    public static IServiceCollection AddAzureBlobLibrarianDatabaseAsSingleton(this IServiceCollection services,
        Func<IServiceProvider, AzureBlobLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (factory is null)
            services.TryAddSingleton<ILibrarianDatabase, AzureBlobLibrarianDatabase>();
        else
            services.TryAddSingleton<ILibrarianDatabase>(provider => factory(provider));
        return services;
    }

    /// <summary>Registers a keyed Azure Blob database as a singleton.</summary>
    /// <remarks>The service key selects the DI instance and does not change the blob address.
    /// Supply a factory for instance-specific storage and authentication; otherwise shared configuration is used.</remarks>
    public static IServiceCollection AddAzureBlobLibrarianDatabaseAsSingleton(this IServiceCollection services,
        object serviceKey, Func<IServiceProvider, AzureBlobLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        if (factory is null)
            services.TryAddKeyedSingleton<ILibrarianDatabase, AzureBlobLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));
        return services;
    }
}
