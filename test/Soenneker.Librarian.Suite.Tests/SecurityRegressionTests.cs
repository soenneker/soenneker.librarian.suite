using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Soenneker.Librarian.Core;
using Soenneker.Librarian.CouchDb;
using Soenneker.Librarian.Maui.Secure;
using Soenneker.Librarian.Memory;
using Soenneker.Utils.HttpClientCache.Abstract;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class SecurityRegressionTests
{
    [Test]
    [Arguments(0xD800)]
    [Arguments(0xD801)]
    [Arguments(0xDBFF)]
    [Arguments(0xDC00)]
    [Arguments(0xDFFF)]
    public async Task Malformed_scopes_cannot_read_create_or_delete_another_scopes_storage(int surrogate, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "librarian-secure-" + Guid.NewGuid());
        const string validScope = "app/account/\uFFFD";
        string invalidScope = "app/account/" + (char)surrogate;
        var storage = new SecureStorageStub();
        var keys = new MauiSecureEncryptionKeyStore(storage);
        try
        {
            await using (var original = new MauiSecureLibrarianDatabase(validScope, storage, NullLogger<MauiSecureLibrarianDatabase>.Instance, directory))
                await (await original.GetContainer("items", cancellationToken)).AddItem("one", "private-message", cancellationToken);
            string path = MauiSecureLibrarianDatabase.GetFilePath(validScope, directory);
            byte[] ciphertext = await File.ReadAllBytesAsync(path, cancellationToken);
            KeyValuePair<string, string> key = storage.Values.Single();
            storage.Fail = true;

            await RejectScope(() => { _ = MauiSecureLibrarianDatabase.GetFilePath(invalidScope, directory); return ValueTask.CompletedTask; });
            await RejectScope(async () => { await using var rejected = new MauiSecureLibrarianDatabase(invalidScope, storage, NullLogger<MauiSecureLibrarianDatabase>.Instance, directory); });
            await RejectScope(async () => { await using var rejected = new EncryptedSnapshotStub(path, invalidScope, keys); });
            await RejectScope(async () => { _ = await keys.GetKey(invalidScope, false, cancellationToken); });
            await RejectScope(async () => { _ = await keys.GetKey(invalidScope, true, cancellationToken); });
            await RejectScope(() => keys.DeleteKey(invalidScope, cancellationToken));
            await RejectScope(() => MauiSecureLibrarianDatabase.DeleteStorage(invalidScope, storage, directory, cancellationToken));

            byte[] after = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!ciphertext.SequenceEqual(after) ||
                storage.Values.Count != 1 || !storage.Values.Single().Equals(key))
                throw new Exception("A malformed scope changed another scope's persisted data or key.");
            storage.Fail = false;
            await using var reopened = new MauiSecureLibrarianDatabase(validScope, storage, NullLogger<MauiSecureLibrarianDatabase>.Instance, directory);
            if (await (await reopened.GetContainer("items", cancellationToken)).GetItem("one", cancellationToken) != "private-message")
                throw new Exception("The valid scope lost its data.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Valid_unicode_scopes_keep_existing_file_key_and_ciphertext_formats(CancellationToken cancellationToken)
    {
        const string scope = "app/\u00e9\U0001f600/tenant\uFFFD";
        string directory = Path.Combine(Path.GetTempPath(), "librarian-secure-" + Guid.NewGuid());
        var storage = new SecureStorageStub();
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
            string path = Path.Combine(directory, hash + ".bin");
            if (MauiSecureLibrarianDatabase.GetFilePath(scope, directory) != path)
                throw new Exception("Existing Unicode file names changed.");
            await using (var database = new MauiSecureLibrarianDatabase(scope, storage, NullLogger<MauiSecureLibrarianDatabase>.Instance, directory))
                await (await database.GetContainer("items", cancellationToken)).AddItem("one", "private-message", cancellationToken);
            byte[] key = Convert.FromBase64String(storage.Values["Librarian.Secure.v1." + hash]);
            byte[] ciphertext = await File.ReadAllBytesAsync(path, cancellationToken);
            byte[] plaintext = new byte[ciphertext.Length - 36];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(ciphertext.AsSpan(8, 12), ciphertext.AsSpan(36), ciphertext.AsSpan(20, 16), plaintext,
                    Encoding.UTF8.GetBytes("Librarian.Secure.v1:" + scope));
                if (!Encoding.UTF8.GetString(plaintext).Contains("private-message", StringComparison.Ordinal))
                    throw new Exception("Existing encrypted format changed.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Repository_writes_never_log_document_bodies(bool logging, CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Librarian:Log"] = logging.ToString()
        }).Build();
        var logger = new RecordingLogger<LibrarianRepository<ExampleDocument>>();
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        var repository = new LibrarianRepository<ExampleDocument>(configuration, logger, database, "items");
        var document = new ExampleDocument { Id = "one", Name = "private-create-token" };
        await repository.AddItem(document, cancellationToken);
        document.Name = "private-update-token";
        await repository.UpdateItem(document, cancellationToken);
        document.Name = "private-failed-create-token";
        try { await repository.AddItem(document, cancellationToken); throw new Exception("Duplicate write unexpectedly succeeded."); }
        catch (InvalidOperationException) { }
        document.Id = "missing";
        document.Name = "private-failed-update-token";
        try { await repository.UpdateItem(document, cancellationToken); throw new Exception("Missing update unexpectedly succeeded."); }
        catch (KeyNotFoundException) { }

        if (logger.Messages.Count != (logging ? 4 : 0) ||
            logger.Messages.Any(message => message.Contains("private-", StringComparison.Ordinal)) ||
            logger.Properties.Any(property => property.Key == "document" || property.Value?.ToString()?.Contains("private-", StringComparison.Ordinal) == true))
            throw new Exception("Document contents reached a formatted or structured log.");
        if (logging && (!logger.Properties.Any(property => property.Key == "method" && Equals(property.Value, "AddItem")) ||
                        !logger.Properties.Any(property => property.Key == "method" && Equals(property.Value, "UpdateItem"))))
            throw new Exception("Operation metadata was lost.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void Remote_couchdb_http_is_rejected_before_io(bool credentials)
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        try
        {
            _ = new CouchDbLibrarianDatabase(new CouchDbLibrarianOptions
            {
                Endpoint = new Uri("http://couch.invalid/"), Username = credentials ? "user" : null,
                Password = credentials ? "private-password" : null
            }, client);
            throw new Exception("Remote plaintext HTTP was accepted without opting in.");
        }
        catch (ArgumentException exception) when (exception.ParamName == "options") { }
        if (handler.Calls != 0) throw new Exception("Credentials were sent before endpoint validation.");
    }

    [Test]
    [Arguments("https://couch.invalid/proxy/", false)]
    [Arguments("http://localhost:5984/", false)]
    [Arguments("http://127.0.0.1:5984/", false)]
    [Arguments("http://[::1]:5984/", false)]
    [Arguments("http://couch.invalid/proxy/", true)]
    public async Task Couchdb_https_loopback_and_explicit_http_opt_in_are_supported(string endpoint, bool allowInsecure, CancellationToken cancellationToken)
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(new CouchDbLibrarianOptions
        {
            Endpoint = new Uri(endpoint), Key = "test", Username = "user", Password = "password", AllowInsecureHttp = allowInsecure
        }, client);
        handler.Expect(HttpMethod.Head, "/librarian-74657374-6974656d73", inspect: (request, _) =>
        {
            if (request.Headers.Authorization?.Scheme != "Basic" || request.RequestUri?.Scheme != new Uri(endpoint).Scheme)
                throw new Exception("An explicitly allowed connection lost authentication or changed its transport.");
        });
        await database.GetContainer("items", cancellationToken);
        handler.Verify();
    }

    [Test]
    [Arguments((string?)null)]
    [Arguments("false")]
    [Arguments("true")]
    public async Task Couchdb_configuration_requires_explicit_http_opt_in(string? allowInsecure)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Librarian:CouchDb:Endpoint"] = "http://couch.invalid/",
            ["Librarian:CouchDb:Username"] = "user",
            ["Librarian:CouchDb:Password"] = "private-password",
            ["Librarian:CouchDb:AllowInsecureHttp"] = allowInsecure
        }).Build();
        var cache = new Mock<IHttpClientCache>();
        try
        {
            await using var database = new CouchDbLibrarianDatabase(configuration, cache.Object);
            if (allowInsecure != "true") throw new Exception("Configuration allowed remote plaintext HTTP without an explicit opt-in.");
        }
        catch (ArgumentException exception) when (allowInsecure != "true" && exception.ParamName == "options") { }
        if (cache.Invocations.Any(call => call.Method.Name == "Get")) throw new Exception("Construction accessed the HTTP client.");
    }

    private static async ValueTask RejectScope(Func<ValueTask> operation)
    {
        try { await operation(); throw new Exception("Malformed scope was accepted."); }
        catch (EncoderFallbackException) { }
    }
}
