using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Blazor.Utils.ModuleImport.Registrars;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.IndexedDb.Registrars;

/// <summary>Registers the Librarian IndexedDb database provider.</summary>
public static class IndexedDbLibrarianDatabaseRegistrar
{
    /// <summary>Adds <see cref="ILibrarianDatabase"/> as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>Uses Librarian:IndexedDb:Key from configuration, defaulting to librarian.
    /// Invoke storage operations after interactive rendering. Save pending writes before navigation.</remarks>
    public static IServiceCollection AddIndexedDbLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.AddModuleImportUtilAsScoped()
                .TryAddScoped<ILibrarianDatabase, IndexedDbLibrarianDatabase>();

        return services;
    }

    /// <summary>Adds a keyed IndexedDb database as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddIndexedDbLibrarianDatabaseAsScoped(this IServiceCollection services, object serviceKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        services.AddModuleImportUtilAsScoped()
                .TryAddKeyedScoped<ILibrarianDatabase, IndexedDbLibrarianDatabase>(serviceKey);

        return services;
    }
}
