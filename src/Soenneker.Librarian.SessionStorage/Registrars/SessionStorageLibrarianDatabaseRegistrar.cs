using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Blazor.Utils.ModuleImport.Registrars;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.SessionStorage.Registrars;

/// <summary>Registers the Librarian SessionStorage database provider.</summary>
public static class SessionStorageLibrarianDatabaseRegistrar
{
    /// <summary>Adds <see cref="ILibrarianDatabase"/> as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>Uses Librarian:SessionStorage:Key from configuration, defaulting to librarian.
    /// Invoke storage operations after interactive rendering. Save pending writes before navigation.</remarks>
    public static IServiceCollection AddSessionStorageLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.AddModuleImportUtilAsScoped()
                .TryAddScoped<ILibrarianDatabase, SessionStorageLibrarianDatabase>();

        return services;
    }

    /// <summary>Adds a keyed SessionStorage database as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddSessionStorageLibrarianDatabaseAsScoped(this IServiceCollection services, object serviceKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        services.AddModuleImportUtilAsScoped()
                .TryAddKeyedScoped<ILibrarianDatabase, SessionStorageLibrarianDatabase>(serviceKey);

        return services;
    }
}
