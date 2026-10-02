using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.MemoryStream.Registrars;

namespace Soenneker.Librarian.FileSystem.Registrars;

/// <summary>
/// Registers the Librarian filesystem database provider.
/// </summary>
public static class FileSystemLibrarianDatabaseRegistrar
{
    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a singleton service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddFileSystemLibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.AddFileUtilAsSingleton()
                .AddMemoryStreamUtilAsSingleton()
                .TryAddSingleton<ILibrarianDatabase, FileSystemLibrarianDatabase>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a scoped service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddFileSystemLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.AddFileUtilAsScoped()
                .AddMemoryStreamUtilAsSingleton()
                .TryAddScoped<ILibrarianDatabase, FileSystemLibrarianDatabase>();

        return services;
    }

    /// <summary>Adds a keyed FileSystem database as a singleton service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddFileSystemLibrarianDatabaseAsSingleton(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, FileSystemLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        services.AddFileUtilAsSingleton().AddMemoryStreamUtilAsSingleton();

        if (factory is null)
            services.TryAddKeyedSingleton<ILibrarianDatabase, FileSystemLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }

    /// <summary>Adds a keyed FileSystem database as a scoped service.</summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <param name="serviceKey">Key used to resolve this database independently of other registrations.</param>
    /// <param name="factory">Optional factory for instance-specific connections and storage settings. When omitted, shared application configuration is used.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    /// <remarks>The service key selects the DI instance; it does not change the underlying storage namespace.
    /// Resolve with GetRequiredKeyedService&lt;ILibrarianDatabase&gt; or inject with FromKeyedServices.</remarks>
    public static IServiceCollection AddFileSystemLibrarianDatabaseAsScoped(this IServiceCollection services, object serviceKey,
        Func<IServiceProvider, FileSystemLibrarianDatabase>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        services.AddFileUtilAsScoped().AddMemoryStreamUtilAsSingleton();

        if (factory is null)
            services.TryAddKeyedScoped<ILibrarianDatabase, FileSystemLibrarianDatabase>(serviceKey);
        else
            services.TryAddKeyedScoped<ILibrarianDatabase>(serviceKey, (provider, _) => factory(provider));

        return services;
    }
}
