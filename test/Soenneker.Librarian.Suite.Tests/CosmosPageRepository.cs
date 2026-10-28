using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class CosmosPageRepository(ICosmosLibrarianDatabaseFactory database, IConfiguration configuration, ILogger<CosmosPageRepository> logger)
    : CosmosLibrarianRepository<NativeDocument>(database, configuration, logger, null!, null!, null!)
{
    public override string ContainerName => "librarian.test.pages";
    public override bool AuditEnabled => false;
}
