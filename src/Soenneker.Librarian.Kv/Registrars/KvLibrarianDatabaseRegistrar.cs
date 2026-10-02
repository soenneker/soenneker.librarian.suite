using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Cloudflare.Workers.Kv.Registrars;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Kv.Registrars;

/// <summary>Registers the single-owner Cloudflare Workers KV snapshot provider.</summary>
public static class KvLibrarianDatabaseRegistrar
{
    /// <summary>Adds the Kv database and its Soenneker Cloudflare client as singleton services.</summary>
    /// <remarks>Requires Librarian:Kv:AccountId, ApiKey, and NamespaceId. Key defaults to librarian.json.
    /// The namespace must exist. Use one owner per key; KV is eventually consistent, including after restart.</remarks>
    public static IServiceCollection AddKvLibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.AddCloudflareWorkersKvUtilAsSingleton().TryAddSingleton<ILibrarianDatabase, KvLibrarianDatabase>();
        return services;
    }

    /// <summary>Adds a keyed Kv database as a singleton service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddKvLibrarianDatabaseAsSingleton(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, KvLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        services.AddCloudflareWorkersKvUtilAsSingleton();

        if (factory is null)
            services.TryAddKeyedSingleton<ILibrarianDatabase, KvLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }
}

