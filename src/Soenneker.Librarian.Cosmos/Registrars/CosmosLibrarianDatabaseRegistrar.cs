using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Cosmos.Container.Registrars;

namespace Soenneker.Librarian.Cosmos.Registrars;

/// <summary>
/// Registers the Librarian Cosmos DB database provider.
/// </summary>
public static class CosmosLibrarianDatabaseRegistrar
{
    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a singleton service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddCosmosLibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.AddCosmosContainerUtilAsSingleton();
        services.TryAddSingleton<ILibrarianDatabase, CosmosLibrarianDatabase>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a scoped service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddCosmosLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.AddCosmosContainerUtilAsSingleton();
        services.TryAddScoped<ILibrarianDatabase, CosmosLibrarianDatabase>();

        return services;
    }

    /// <summary>Adds a keyed Cosmos database as a singleton service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddCosmosLibrarianDatabaseAsSingleton(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, CosmosLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);

        services.AddCosmosContainerUtilAsSingleton();
        if (factory is null)
            services.TryAddKeyedSingleton<ILibrarianDatabase, CosmosLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }

    /// <summary>Adds a keyed Cosmos database as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddCosmosLibrarianDatabaseAsScoped(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, CosmosLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);

        services.AddCosmosContainerUtilAsSingleton();
        if (factory is null)
            services.TryAddKeyedScoped<ILibrarianDatabase, CosmosLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedScoped<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }
}
