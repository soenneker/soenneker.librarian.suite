using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Blazor.Utils.ModuleImport.Registrars;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.LocalStorage.Registrars;

/// <summary>Registers the Librarian LocalStorage database provider.</summary>
public static class LocalStorageLibrarianDatabaseRegistrar
{
    /// <summary>Adds <see cref="ILibrarianDatabase"/> as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>Uses Librarian:LocalStorage:Key from configuration, defaulting to librarian.
    /// Invoke storage operations after interactive rendering. Save pending writes before navigation.</remarks>
    public static IServiceCollection AddLocalStorageLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.AddModuleImportUtilAsScoped()
                .TryAddScoped<ILibrarianDatabase, LocalStorageLibrarianDatabase>();

        return services;
    }

    /// <summary>Adds a keyed LocalStorage database as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddLocalStorageLibrarianDatabaseAsScoped(this IServiceCollection services, object serviceKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        services.AddModuleImportUtilAsScoped()
                .TryAddKeyedScoped<ILibrarianDatabase, LocalStorageLibrarianDatabase>(serviceKey);

        return services;
    }
}
