using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Memory.Registrars;

/// <summary>
/// Registers the Librarian memory database provider.
/// </summary>
public static class MemoryLibrarianDatabaseRegistrar
{
    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a singleton service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddMemoryLibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.TryAddSingleton<ILibrarianDatabase, MemoryLibrarianDatabase>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a scoped service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddMemoryLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.TryAddScoped<ILibrarianDatabase, MemoryLibrarianDatabase>();

        return services;
    }
}
