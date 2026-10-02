using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Postgres.Registrars;

/// <summary>
/// Registers the Librarian PostgreSQL database provider.
/// </summary>
public static class PostgresLibrarianDatabaseRegistrar
{
    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a singleton service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddPostgresLibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.TryAddSingleton<ILibrarianDatabase, PostgresLibrarianDatabase>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a scoped service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddPostgresLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.TryAddScoped<ILibrarianDatabase, PostgresLibrarianDatabase>();

        return services;
    }

    /// <summary>Adds a keyed Postgres database as a singleton service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddPostgresLibrarianDatabaseAsSingleton(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, PostgresLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);

        if (factory is null)
            services.TryAddKeyedSingleton<ILibrarianDatabase, PostgresLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }

    /// <summary>Adds a keyed Postgres database as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddPostgresLibrarianDatabaseAsScoped(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, PostgresLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);

        if (factory is null)
            services.TryAddKeyedScoped<ILibrarianDatabase, PostgresLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedScoped<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }
}
