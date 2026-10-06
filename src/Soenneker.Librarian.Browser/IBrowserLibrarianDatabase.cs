using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Browser;

/// <summary>A browser snapshot owner. Reads are cached; writes detect changes made by other owners.</summary>
/// <remarks>Use one owner per storage key. After a conflict or uncertain interop result, discard and reopen to reload.
/// Ordinary writes require Save; batches persist before publication. Invoke only after interactive rendering, never during prerender.
/// JavaScript modules are loaded and owned by the scoped IModuleImportUtil; disposing a database does not unload shared modules.
/// Browser storage is not a secret store. Quota, denied access and unavailable browser features propagate as errors.</remarks>
public interface IBrowserLibrarianDatabase : ILibrarianDatabase
{
    /// <summary>Discards unsaved changes and disposes this owner without writing browser storage.</summary>
    /// <remarks>Stop all operations before calling. Persisted data is left intact and a fresh instance may reopen it.</remarks>
    ValueTask DiscardAsync();
}
