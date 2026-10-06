using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Blazor.Utils.ModuleImport.Abstract;
using Soenneker.Librarian.Browser;

namespace Soenneker.Librarian.SessionStorage;

public sealed class SessionStorageLibrarianDatabase : BrowserSnapshotLibrarianDatabase
{
    public SessionStorageLibrarianDatabase(IConfiguration configuration, IModuleImportUtil moduleImportUtil,
        ILogger<SessionStorageLibrarianDatabase> logger)
        : this(moduleImportUtil, logger, configuration["Librarian:SessionStorage:Key"] ?? "librarian")
    {
    }

    public SessionStorageLibrarianDatabase(IModuleImportUtil moduleImportUtil, ILogger<SessionStorageLibrarianDatabase> logger, string key)
        : base(moduleImportUtil, logger, "sessionStorage", key)
    {
    }
}
