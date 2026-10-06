using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Mongo.Registrars;

/// <summary>
/// Registers the Librarian Mongo database provider.
/// </summary>
public static class MongoLibrarianDatabaseRegistrar
{
    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a singleton service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime BSON serializer discovery constructs generic types. Supply explicit serializer factories instead.")]
    public static IServiceCollection AddMongoLibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.TryAddSingleton<ILibrarianDatabase, MongoLibrarianDatabase>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a scoped service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime BSON serializer discovery constructs generic types. Supply explicit serializer factories instead.")]
    public static IServiceCollection AddMongoLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.TryAddScoped<ILibrarianDatabase, MongoLibrarianDatabase>();

        return services;
    }

    /// <summary>Adds a keyed Mongo database as a singleton service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime BSON serializer discovery constructs generic types. Supply explicit serializer factories instead.")]
    public static IServiceCollection AddMongoLibrarianDatabaseAsSingleton(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, MongoLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);

        if (factory is null)
            services.TryAddKeyedSingleton<ILibrarianDatabase, MongoLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }

    /// <summary>Adds a keyed Mongo database as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime BSON serializer discovery constructs generic types. Supply explicit serializer factories instead.")]
    public static IServiceCollection AddMongoLibrarianDatabaseAsScoped(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, MongoLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);

        if (factory is null)
            services.TryAddKeyedScoped<ILibrarianDatabase, MongoLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedScoped<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }
}
