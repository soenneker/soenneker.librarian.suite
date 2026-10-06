using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Maui.Secure;

/// <summary>An encrypted, single-owner filesystem database with keys held in platform secure storage.</summary>
/// <remarks>Use an application/user/tenant-specific scope. Save after important writes and before suspension;
/// mobile process termination does not guarantee disposal. Ciphertext or key errors never reset persisted data automatically.</remarks>
public interface IMauiSecureLibrarianDatabase : ILibrarianDatabase
{
    /// <summary>Discards pending writes and permanently disposes this owner without accessing secure storage or writing files.</summary>
    /// <remarks>Stop operations first. This allows logout or recovery when secure storage is unavailable.
    /// Persisted ciphertext and keys remain until DeleteStorage is called.</remarks>
    ValueTask DiscardAsync();
}
