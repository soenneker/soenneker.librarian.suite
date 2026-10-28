using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Utils.UserContext.Abstract;
using Soenneker.Utils.BackgroundQueue.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Documents.Typed;
using System.Linq;
using Soenneker.Cosmos.Repositories.Shared.Abstract;
namespace Soenneker.Librarian.Cosmos;
public abstract class SharedCosmosLibrarianRepository<TDocument> : CosmosLibrarianRepository<TDocument>, ISharedRepository<TDocument> where TDocument : TypedDocument
{
    protected abstract string EntityType { get; }
    protected override bool HasScope => true;
    protected override bool InScope(TDocument document) => document.EntityType == EntityType;
    protected override IQueryable<TDocument> Scope(IQueryable<TDocument> query) => query.Where(document => document.EntityType == EntityType);
    protected SharedCosmosLibrarianRepository(ICosmosLibrarianDatabaseFactory database, IConfiguration config, ILogger logger, IUserContext userContext,
        IBackgroundQueue queue, IMemoryStreamUtil streams) : base(database, config, logger, userContext, queue, streams) { }
}
