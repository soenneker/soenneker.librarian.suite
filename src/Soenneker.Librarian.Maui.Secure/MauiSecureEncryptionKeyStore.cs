using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;
using Soenneker.Librarian.Core.Security;

namespace Soenneker.Librarian.Maui.Secure;

public sealed class MauiSecureEncryptionKeyStore(ISecureStorage storage) : ILibrarianEncryptionKeyStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async ValueTask<byte[]> GetKey(string scope, bool allowCreate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string name = GetKeyName(scope);
            string? stored = await storage.GetAsync(name).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (stored is not null)
            {
                byte[] existing = Convert.FromBase64String(stored);
                if (existing.Length == 32) return existing;
                CryptographicOperations.ZeroMemory(existing);
                throw new CryptographicException("The protected Librarian key is invalid.");
            }
            if (!allowCreate) throw new CryptographicException("The protected Librarian key is missing.");
            byte[] key = RandomNumberGenerator.GetBytes(32);
            try
            {
                await storage.SetAsync(name, Convert.ToBase64String(key)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return key;
            }
            catch { CryptographicOperations.ZeroMemory(key); throw; }
        }
        finally { Gate.Release(); }
    }

    public async ValueTask DeleteKey(string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { storage.Remove(GetKeyName(scope)); }
        finally { Gate.Release(); }
    }

    private static string GetKeyName(string scope) => "Librarian.Secure.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
}
