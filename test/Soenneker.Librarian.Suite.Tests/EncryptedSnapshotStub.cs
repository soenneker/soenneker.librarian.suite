using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Core.Security;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class EncryptedSnapshotStub(string path, string scope, ILibrarianEncryptionKeyStore keys)
    : EncryptedFileSnapshotLibrarianDatabase(path, scope, keys, NullLogger.Instance);
