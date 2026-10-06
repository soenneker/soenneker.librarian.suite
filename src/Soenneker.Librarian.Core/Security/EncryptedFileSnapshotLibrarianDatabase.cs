using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Soenneker.Librarian.Core.Security;

/// <summary>A single-owner snapshot database with authenticated encryption and atomic file replacement.</summary>
/// <remarks>Use a stable, unambiguous scope identifying the application, user and tenant. One owner may use each file.
/// Ordinary writes require Save; batches persist immediately. Missing keys or corrupt ciphertext throw and never reset storage.</remarks>
public abstract class EncryptedFileSnapshotLibrarianDatabase : SnapshotLibrarianDatabase
{
    private static readonly byte[] Magic = "LIBSEC01"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _path;
    private readonly string _scope;
    private readonly ILibrarianEncryptionKeyStore _keys;

    protected EncryptedFileSnapshotLibrarianDatabase(string filePath, string scope, ILibrarianEncryptionKeyStore keys, ILogger logger) : base(logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(keys);
        _path = Path.GetFullPath(filePath);
        _scope = scope;
        _keys = keys;
    }

    protected override async ValueTask<string?> ReadSnapshot(CancellationToken cancellationToken)
    {
        byte[] encrypted;
        try { encrypted = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        int headerSize = Magic.Length + NonceSize + TagSize;
        if (encrypted.Length <= headerSize || !encrypted.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Invalid encrypted Librarian snapshot.");
        byte[] key = await _keys.GetKey(_scope, false, cancellationToken).ConfigureAwait(false);
        byte[] plaintext = new byte[encrypted.Length - headerSize];
        try
        {
            ValidateKey(key);
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(encrypted.AsSpan(Magic.Length, NonceSize), encrypted.AsSpan(headerSize),
                encrypted.AsSpan(Magic.Length + NonceSize, TagSize), plaintext, GetAssociatedData());
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    protected override async ValueTask WriteSnapshot(string json, CancellationToken cancellationToken)
    {
        byte[] key = await _keys.GetKey(_scope, !File.Exists(_path), cancellationToken).ConfigureAwait(false);
        byte[] plaintext = Encoding.UTF8.GetBytes(json);
        string temporary = _path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            ValidateKey(key);
            byte[] encrypted = new byte[Magic.Length + NonceSize + TagSize + plaintext.Length];
            Magic.CopyTo(encrypted, 0);
            RandomNumberGenerator.Fill(encrypted.AsSpan(Magic.Length, NonceSize));
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(encrypted.AsSpan(Magic.Length, NonceSize), plaintext,
                    encrypted.AsSpan(Magic.Length + NonceSize + TagSize), encrypted.AsSpan(Magic.Length + NonceSize, TagSize), GetAssociatedData());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private byte[] GetAssociatedData() => Encoding.UTF8.GetBytes("Librarian.Secure.v1:" + _scope);

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != 32) throw new CryptographicException("Librarian requires a 256-bit encryption key.");
    }
}
