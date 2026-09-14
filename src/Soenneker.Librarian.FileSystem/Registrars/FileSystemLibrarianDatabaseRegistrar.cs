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
}
