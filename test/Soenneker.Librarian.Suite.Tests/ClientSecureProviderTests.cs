using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Maui.Secure;

namespace Soenneker.Librarian.Suite.Tests;

public class ClientSecureProviderTests
{
    private static MauiSecureLibrarianDatabase Open(SecureStorageStub storage, string directory, string scope = "app/user/tenant") =>
        new(scope, storage, NullLogger<MauiSecureLibrarianDatabase>.Instance, directory);

    [Test]
    public async Task Encrypted_roundtrip_batches_scope_isolation_and_logout(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "librarian-secure-" + Guid.NewGuid());
        var storage = new SecureStorageStub();
        try
        {
            await using (var db = Open(storage, directory))
            {
                var items = await db.GetContainer("messages", cancellationToken: cancellationToken);
                await items.AddItem("one", "private-message", cancellationToken: cancellationToken);
                await db.Save(cancellationToken: cancellationToken);
                string path = MauiSecureLibrarianDatabase.GetFilePath("app/user/tenant", directory);
                if (Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, cancellationToken: cancellationToken)).Contains("private-message")) throw new Exception("Plaintext persisted");
                await db.Execute(new LibrarianBatch([new LibrarianWrite("messages", "two", "batch-message")]), cancellationToken: cancellationToken);
                await db.UnloadContainer("messages", cancellationToken: cancellationToken);
                if (await (await db.GetContainer("messages", cancellationToken: cancellationToken)).GetItem("two", cancellationToken: cancellationToken) != "batch-message") throw new Exception("Batch lost");
            }
            await using (var reopened = Open(storage, directory))
                if (await (await reopened.GetContainer("messages", cancellationToken: cancellationToken)).GetItem("one", cancellationToken: cancellationToken) != "private-message") throw new Exception("Reopen lost data");
            await using (var other = Open(storage, directory, "app/other/tenant"))
            {
                var items = await other.GetContainer("messages", cancellationToken: cancellationToken);
                if (await items.GetItem("one", cancellationToken: cancellationToken) is not null) throw new Exception("Account data leaked");
                await items.AddItem("one", "other-account", cancellationToken: cancellationToken);
            }
            await MauiSecureLibrarianDatabase.DeleteStorage("app/user/tenant", storage, directory, cancellationToken: cancellationToken);
            if (File.Exists(MauiSecureLibrarianDatabase.GetFilePath("app/user/tenant", directory)) || storage.Values.Count != 1)
                throw new Exception("Logout did not isolate deletion");
            await using (var other = Open(storage, directory, "app/other/tenant"))
                if (await (await other.GetContainer("messages", cancellationToken: cancellationToken)).GetItem("one", cancellationToken: cancellationToken) != "other-account") throw new Exception("Other account deleted");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Tampering_missing_keys_and_unavailable_secure_storage_fail_closed(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "librarian-secure-" + Guid.NewGuid());
        var storage = new SecureStorageStub();
        try
        {
            await using (var db = Open(storage, directory))
                await (await db.GetContainer("items", cancellationToken: cancellationToken)).AddItem("one", "secret", cancellationToken: cancellationToken);
            string path = MauiSecureLibrarianDatabase.GetFilePath("app/user/tenant", directory);
            byte[] original = await File.ReadAllBytesAsync(path, cancellationToken: cancellationToken);
            byte[] tampered = (byte[])original.Clone();
            tampered[^1] ^= 1;
            await File.WriteAllBytesAsync(path, tampered, cancellationToken: cancellationToken);
            await using (var db = Open(storage, directory))
            {
                try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Tampering accepted"); }
                catch (CryptographicException) { }
            }
            await File.WriteAllBytesAsync(path, original, cancellationToken: cancellationToken);
            storage.Values.Clear();
            await using (var db = Open(storage, directory))
            {
                try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Missing key accepted"); }
                catch (CryptographicException) { }
            }
            byte[] afterMissingKey = await File.ReadAllBytesAsync(path, cancellationToken: cancellationToken);
            if (storage.Values.Count != 0 || !original.SequenceEqual(afterMissingKey)) throw new Exception("Missing key reset storage");
            storage.Fail = true;
            var fresh = Open(storage, directory, "new");
            await (await fresh.GetContainer("items", cancellationToken: cancellationToken)).AddItem("one", "secret", cancellationToken: cancellationToken);
            try { await fresh.Save(cancellationToken: cancellationToken); throw new Exception("Unavailable storage accepted"); }
            catch (InvalidOperationException) { }
            if (File.Exists(MauiSecureLibrarianDatabase.GetFilePath("new", directory))) throw new Exception("Fallback wrote a file");
            storage.Fail = false;
            await fresh.DisposeAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Scope_binding_rejects_copied_ciphertext_even_with_the_same_key(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "librarian-secure-" + Guid.NewGuid());
        var storage = new SecureStorageStub();
        try
        {
            await using (var source = Open(storage, directory))
                await (await source.GetContainer("items", cancellationToken: cancellationToken)).AddItem("one", "secret", cancellationToken: cancellationToken);
            string encodedKey = storage.Values.Values.Single();
            var keys = new MauiSecureEncryptionKeyStore(storage);
            byte[] otherKey = await keys.GetKey("other", true, cancellationToken: cancellationToken);
            CryptographicOperations.ZeroMemory(otherKey);
            foreach (string name in storage.Values.Keys.ToArray()) storage.Values[name] = encodedKey;
            File.Copy(MauiSecureLibrarianDatabase.GetFilePath("app/user/tenant", directory),
                MauiSecureLibrarianDatabase.GetFilePath("other", directory));
            await using var other = Open(storage, directory, "other");
            try { await other.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Ciphertext crossed account scopes"); }
            catch (CryptographicException) { }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Invalid_key_is_not_replaced_and_discard_does_not_flush(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "librarian-secure-" + Guid.NewGuid());
        var storage = new SecureStorageStub();
        try
        {
            var keys = new MauiSecureEncryptionKeyStore(storage);
            byte[] key = await keys.GetKey("app/user/tenant", true, cancellationToken: cancellationToken);
            CryptographicOperations.ZeroMemory(key);
            string name = storage.Values.Keys.Single();
            storage.Values[name] = Convert.ToBase64String(new byte[8]);
            var db = Open(storage, directory);
            var items = await db.GetContainer("items", cancellationToken: cancellationToken);
            await items.AddItem("one", "secret", cancellationToken: cancellationToken);
            try { await db.Save(cancellationToken: cancellationToken); throw new Exception("Invalid key accepted"); }
            catch (CryptographicException) { }
            if (storage.Values[name] != Convert.ToBase64String(new byte[8])) throw new Exception("Invalid key replaced");
            await db.DiscardAsync();
            await db.DisposeAsync();
            if (File.Exists(MauiSecureLibrarianDatabase.GetFilePath("app/user/tenant", directory))) throw new Exception("Discard wrote data");
            try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Discarded database reused"); }
            catch (ObjectDisposedException) { }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Cancellation_and_failed_save_preserve_previous_ciphertext(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "librarian-secure-" + Guid.NewGuid());
        var storage = new SecureStorageStub();
        try
        {
            await using var db = Open(storage, directory);
            var items = await db.GetContainer("items", cancellationToken: cancellationToken);
            await items.AddItem("one", "before", cancellationToken: cancellationToken);
            await db.Save(cancellationToken: cancellationToken);
            string path = MauiSecureLibrarianDatabase.GetFilePath("app/user/tenant", directory);
            byte[] original = await File.ReadAllBytesAsync(path, cancellationToken: cancellationToken);
            await items.UpdateItem("one", "after", cancellationToken: cancellationToken);
            try { await db.Save(new CancellationToken(true)); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            storage.Fail = true;
            try { await db.Save(cancellationToken: cancellationToken); throw new Exception("Failure ignored"); }
            catch (InvalidOperationException) { }
            finally { storage.Fail = false; }
            byte[] afterFailure = await File.ReadAllBytesAsync(path, cancellationToken: cancellationToken);
            if (!original.SequenceEqual(afterFailure)) throw new Exception("Failed write replaced ciphertext");
            await db.Save(cancellationToken: cancellationToken);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
