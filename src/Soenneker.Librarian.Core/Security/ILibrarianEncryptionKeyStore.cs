using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Core.Security;

/// <summary>Provides platform-protected keys for encrypted snapshot databases.</summary>
public interface ILibrarianEncryptionKeyStore
{
    /// <summary>Returns an owned copy of a 32-byte key. Creates it only when allowed; otherwise a missing key must throw.</summary>
    /// <remarks>The caller clears the returned bytes. Implementations must never use a plaintext fallback or replace an existing invalid key.</remarks>
    ValueTask<byte[]> GetKey(string scope, bool allowCreate, CancellationToken cancellationToken = default);

    /// <summary>Deletes this scope's key. Stop and dispose all owners and delete their encrypted files first.</summary>
    ValueTask DeleteKey(string scope, CancellationToken cancellationToken = default);
}
