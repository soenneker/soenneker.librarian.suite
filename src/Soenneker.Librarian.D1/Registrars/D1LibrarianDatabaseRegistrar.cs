using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Cloudflare.D1.Registrars;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.D1.Registrars;

/// <summary>Registers the single-owner Cloudflare D1 snapshot provider.</summary>
public static class D1LibrarianDatabaseRegistrar
{
    /// <summary>Adds the D1 database and its Soenneker Cloudflare client as singleton services.</summary>
    /// <remarks>Requires Librarian:D1:AccountId, ApiKey, and DatabaseId. Name defaults to librarian.
    /// The D1 database must exist; the snapshot table is created automatically. Only one owner may use each name.</remarks>
    public static IServiceCollection AddD1LibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.AddCloudflareD1UtilAsSingleton().TryAddSingleton<ILibrarianDatabase, D1LibrarianDatabase>();
        return services;
    }

    /// <summary>Adds a keyed D1 database as a singleton service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddD1LibrarianDatabaseAsSingleton(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, D1LibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        services.AddCloudflareD1UtilAsSingleton();

        if (factory is null)
            services.TryAddKeyedSingleton<ILibrarianDatabase, D1LibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }
}
