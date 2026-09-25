using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Cloudflare.R2.Registrars;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.R2.Registrars;

/// <summary>Registers the single-owner Cloudflare R2 snapshot provider.</summary>
public static class R2LibrarianDatabaseRegistrar
{
    /// <summary>Adds the R2 database and its Soenneker Cloudflare client as singleton services.</summary>
    /// <remarks>Requires Librarian:R2:AccountId and BucketName. ObjectKey defaults to librarian.json.
    /// Librarian:R2:ApiKey overrides Cloudflare:ApiKey. The bucket must exist; only one owner may use each object key.</remarks>
    public static IServiceCollection AddR2LibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.AddCloudflareR2UtilAsSingleton().TryAddSingleton<ILibrarianDatabase, R2LibrarianDatabase>();
        return services;
    }
}
