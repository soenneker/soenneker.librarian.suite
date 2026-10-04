using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Cloudflare.Workers.Kv.Abstract;
using Soenneker.Cloudflare.OpenApiClient.Models;
using Soenneker.Librarian.Core;

namespace Soenneker.Librarian.Kv;

public sealed class KvLibrarianDatabase : SnapshotLibrarianDatabase
{
    private readonly ICloudflareWorkersKvUtil _kv;
    private readonly string _accountId;
    private readonly string _apiKey;
    private readonly string _namespaceId;
    private readonly string _key;

    public KvLibrarianDatabase(IConfiguration configuration, ICloudflareWorkersKvUtil kv,
        ILogger<KvLibrarianDatabase> logger) : this(Required(configuration, "AccountId"),
        Required(configuration, "ApiKey"), Required(configuration, "NamespaceId"), kv, logger,
        configuration["Librarian:Kv:Key"] ?? "librarian.json")
    {
    }

    public KvLibrarianDatabase(string accountId, string apiKey, string namespaceId, ICloudflareWorkersKvUtil kv,
        ILogger logger, string key = "librarian.json") : base(logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(kv);
        if (key is "." or ".." || Encoding.UTF8.GetByteCount(key) > 512)
            throw new ArgumentException("KV keys must be at most 512 UTF-8 bytes and cannot be '.' or '..'.",
                nameof(key));
        _accountId = accountId;
        _apiKey = apiKey;
        _namespaceId = namespaceId;
        _key = key;
        _kv = kv;
    }

    private static string Required(IConfiguration configuration, string key) => configuration[$"Librarian:Kv:{key}"] ??
        throw new InvalidOperationException($"Missing configuration: Librarian:Kv:{key}");

    protected override async ValueTask<string?> ReadSnapshot(CancellationToken cancellationToken)
    {
        Stream? stream = await _kv.GetValue(_accountId, _apiKey, _namespaceId, _key, cancellationToken).NoSync();
        if (stream is null)
            return null;

        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return await reader.ReadToEndAsync(cancellationToken).NoSync();
        }
        finally
        {
            await stream.DisposeAsync().NoSync();
        }
    }

    protected override async ValueTask WriteSnapshot(string json, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(json) > 25 * 1024 * 1024)
            throw new InvalidOperationException(
                "The KV Librarian snapshot exceeds the supported 25 MiB size. Use R2 for larger snapshots.");
        // PutValue does not expose the response envelope; bulk writes allow us to verify success.
        WorkersKvNamespaceWriteMultipleKeyValuePairs200? response = await _kv.BulkPut(_accountId, _apiKey, _namespaceId,
            [new WorkersKvBulkWriteItem { Key = _key, Value = json, Base64 = false }], cancellationToken).NoSync();
        if (response?.Success != true || response.Errors is { Count: > 0 })
            throw new InvalidDataException("KV did not confirm the Librarian snapshot upload.");
    }
}