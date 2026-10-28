using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Utils.UserContext.Abstract;
using Soenneker.Utils.BackgroundQueue.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Documents.Typed;
using System.Linq;
using Soenneker.Cosmos.Repositories.General.Abstract;
namespace Soenneker.Librarian.Cosmos;
public abstract class GeneralCosmosLibrarianRepository<TDocument> : SharedCosmosLibrarianRepository<TDocument>, IGeneralRepository<TDocument> where TDocument : TypedDocument
{
    public override string ContainerName => "general";
    protected GeneralCosmosLibrarianRepository(ICosmosLibrarianDatabaseFactory database, IConfiguration config, ILogger logger, IUserContext userContext,
        IBackgroundQueue queue, IMemoryStreamUtil streams) : base(database, config, logger, userContext, queue, streams) { }
}
