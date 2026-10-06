using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using Soenneker.Librarian.Core.Security;

namespace Soenneker.Librarian.Maui.Secure;

public sealed class MauiSecureLibrarianDatabase : EncryptedFileSnapshotLibrarianDatabase, IMauiSecureLibrarianDatabase
{
    public MauiSecureLibrarianDatabase(string scope, ISecureStorage secureStorage, ILogger<MauiSecureLibrarianDatabase> logger,
        string? directoryPath = null) : base(GetFilePath(scope, directoryPath), scope, new MauiSecureEncryptionKeyStore(secureStorage), logger) { }

    public ValueTask DiscardAsync() => DisposeWithoutSaving();

    /// <summary>Gets the scope-specific encrypted file path, under app data unless an explicit directory is supplied.</summary>
    public static string GetFilePath(string scope, string? directoryPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        return Path.Combine(directoryPath ?? Path.Combine(FileSystem.Current.AppDataDirectory, "librarian"), hash + ".bin");
    }

    /// <summary>Deletes one scope's persisted data and protected key, for logout or cache reset.</summary>
    /// <remarks>Stop operations and dispose every database using this scope before calling. Failure propagates;
    /// no other scope is removed. Do not reopen the scope until deletion completes.</remarks>
    public static async ValueTask DeleteStorage(string scope, ISecureStorage secureStorage, string? directoryPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(GetFilePath(scope, directoryPath));
        await new MauiSecureEncryptionKeyStore(secureStorage).DeleteKey(scope, cancellationToken).ConfigureAwait(false);
    }
}
