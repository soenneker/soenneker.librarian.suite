using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Blazor.Utils.ModuleImport.Abstract;
using Soenneker.Librarian.Browser;

namespace Soenneker.Librarian.LocalStorage;

public sealed class LocalStorageLibrarianDatabase : BrowserSnapshotLibrarianDatabase
{
    public LocalStorageLibrarianDatabase(IConfiguration configuration, IModuleImportUtil moduleImportUtil,
        ILogger<LocalStorageLibrarianDatabase> logger)
        : this(moduleImportUtil, logger, configuration["Librarian:LocalStorage:Key"] ?? "librarian")
    {
    }

    public LocalStorageLibrarianDatabase(IModuleImportUtil moduleImportUtil, ILogger<LocalStorageLibrarianDatabase> logger, string key)
        : base(moduleImportUtil, logger, "localStorage", key)
    {
    }
}
