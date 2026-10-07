using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.AzureBlob;

/// <summary>A single-owner database persisted as one Azure block blob containing a JSON snapshot.</summary>
/// <remarks>The Azure container must already exist. Mutations persist on Save, UnloadContainer, or disposal.
/// ETag conditions reject external changes with an Azure RequestFailedException; snapshots are not merged.
/// Supply a BlobClient directly to use managed identity, SAS, or custom client options.</remarks>
public interface IAzureBlobLibrarianDatabase : ILibrarianDatabase
{
    /// <summary>Discards pending changes and disposes the database after a storage conflict.</summary>
    /// <remarks>Stop container operations first. Open a new database instance to load the current snapshot.</remarks>
    ValueTask DiscardAndDispose();
}
