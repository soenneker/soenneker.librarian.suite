using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Soenneker.Cloudflare.R2.Abstract;
using Soenneker.Librarian.Core;

namespace Soenneker.Librarian.R2;

public sealed class R2LibrarianDatabase : SnapshotLibrarianDatabase
{
    private readonly ICloudflareR2Util _r2;
    private readonly string _accountId;
    private readonly string _bucketName;
    private readonly string _objectKey;
    private readonly string? _apiKey;

    public R2LibrarianDatabase(IConfiguration configuration, ICloudflareR2Util r2, ILogger<R2LibrarianDatabase> logger)
        : this(Required(configuration, "AccountId"), Required(configuration, "BucketName"),
            configuration["Librarian:R2:ObjectKey"] ?? "librarian.json", r2, logger, configuration["Librarian:R2:ApiKey"]) { }

    public R2LibrarianDatabase(string accountId, string bucketName, string objectKey, ICloudflareR2Util r2,
        ILogger logger, string? apiKey = null) : base(logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(r2);
        _accountId = accountId;
        _bucketName = bucketName;
        _objectKey = objectKey;
        _apiKey = apiKey;
        _r2 = r2;
    }

    private static string Required(IConfiguration configuration, string key) => configuration[$"Librarian:R2:{key}"] ??
        throw new InvalidOperationException($"Missing configuration: Librarian:R2:{key}");

    protected override async ValueTask<string?> ReadSnapshot(CancellationToken cancellationToken)
    {
        Stream? stream;
        try
        {
            stream = await _r2.GetObject(_accountId, _bucketName, _objectKey, _apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException exception) when (exception.ResponseStatusCode == 404)
        {
            return null;
        }
        // Only a 404 proves absence; an empty successful response must not erase existing data.
        if (stream is null) throw new InvalidDataException("R2 returned an empty object response.");
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async ValueTask WriteSnapshot(string json, CancellationToken cancellationToken)
    {
        var response = await _r2.PutObject(_accountId, _bucketName, _objectKey, json,
            "application/json; charset=utf-8", _apiKey, cancellationToken).ConfigureAwait(false);
        if (response?.Success != true || response.Errors is { Count: > 0 })
            throw new InvalidDataException("R2 did not confirm the Librarian snapshot upload.");
    }
}
