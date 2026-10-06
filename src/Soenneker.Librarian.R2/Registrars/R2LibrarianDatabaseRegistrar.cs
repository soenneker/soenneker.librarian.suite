using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Cloudflare.R2.Registrars;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.R2.Abstract;

namespace Soenneker.Librarian.R2.Registrars;

/// <summary>Registers the single-owner Cloudflare R2 snapshot provider.</summary>
public static class R2LibrarianDatabaseRegistrar
{
    /// <summary>Registers the multi-owner, per-document R2 provider. Register IR2LibrarianObjectStore first.</summary>
    /// <remarks>The database key isolates object prefixes. Writes are immediate and conditional; deletes retain tombstones.
    /// This provider supports versioned mutations, but not secondary indexes, LINQ or multi-document transactions.</remarks>
    public static IServiceCollection AddR2ObjectLibrarianDatabaseAsSingleton(this IServiceCollection services, string key = "librarian")
    {
        services.TryAddSingleton<ILibrarianDatabase>(provider => new R2ObjectLibrarianDatabase(
            provider.GetRequiredService<IR2LibrarianObjectStore>(), key));
        return services;
    }

    /// <summary>Adds the R2 database and its Soenneker Cloudflare client as singleton services.</summary>
    /// <remarks>Requires Librarian:R2:AccountId and BucketName. ObjectKey defaults to librarian.json.
    /// Librarian:R2:ApiKey overrides Cloudflare:ApiKey. The bucket must exist; only one owner may use each object key.</remarks>
    public static IServiceCollection AddR2LibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.AddCloudflareR2UtilAsSingleton().TryAddSingleton<ILibrarianDatabase, R2LibrarianDatabase>();
        return services;
    }

    /// <summary>Adds a keyed R2 database as a singleton service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddR2LibrarianDatabaseAsSingleton(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, R2LibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        services.AddCloudflareR2UtilAsSingleton();

        if (factory is null)
            services.TryAddKeyedSingleton<ILibrarianDatabase, R2LibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }
}
