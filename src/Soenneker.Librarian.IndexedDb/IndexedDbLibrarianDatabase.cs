using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Blazor.Utils.ModuleImport.Abstract;
using Soenneker.Librarian.Browser;

namespace Soenneker.Librarian.IndexedDb;

public sealed class IndexedDbLibrarianDatabase : BrowserSnapshotLibrarianDatabase
{
    public IndexedDbLibrarianDatabase(IConfiguration configuration, IModuleImportUtil moduleImportUtil,
        ILogger<IndexedDbLibrarianDatabase> logger)
        : this(moduleImportUtil, logger, configuration["Librarian:IndexedDb:Key"] ?? "librarian")
    {
    }

    public IndexedDbLibrarianDatabase(IModuleImportUtil moduleImportUtil, ILogger<IndexedDbLibrarianDatabase> logger, string key)
        : base(moduleImportUtil, logger, "indexedDb", key)
    {
    }
}
