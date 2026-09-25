using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Soenneker.Cloudflare.OpenApiClient.Models;
using Soenneker.Cloudflare.Utils.Client.Abstract;
using Soenneker.Librarian.Core;

namespace Soenneker.Librarian.D1;

public sealed class D1LibrarianDatabase : SnapshotLibrarianDatabase
{
    private readonly ICloudflareClientUtil _clientUtil;
    private readonly string _accountId;
    private readonly string _apiKey;
    private readonly string _databaseId;
    private readonly string _name;
    private bool _initialized;

    public D1LibrarianDatabase(IConfiguration configuration, ICloudflareClientUtil clientUtil, ILogger<D1LibrarianDatabase> logger)
        : this(Required(configuration, "AccountId"), Required(configuration, "ApiKey"), Required(configuration, "DatabaseId"),
            clientUtil, logger, configuration["Librarian:D1:Name"] ?? "librarian") { }

    public D1LibrarianDatabase(string accountId, string apiKey, string databaseId, ICloudflareClientUtil clientUtil,
        ILogger logger, string name = "librarian") : base(logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(clientUtil);
        _accountId = accountId;
        _apiKey = apiKey;
        _databaseId = databaseId;
        _name = name;
        _clientUtil = clientUtil;
    }

    private static string Required(IConfiguration configuration, string key) => configuration[$"Librarian:D1:{key}"] ??
        throw new InvalidOperationException($"Missing configuration: Librarian:D1:{key}");

    protected override async ValueTask<string?> ReadSnapshot(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            using JsonDocument created = await Query(
                "CREATE TABLE IF NOT EXISTS librarian_snapshots (name TEXT PRIMARY KEY, value TEXT NOT NULL)", [], cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        using JsonDocument response = await Query("SELECT value FROM librarian_snapshots WHERE name = ?", [_name], cancellationToken).ConfigureAwait(false);
        JsonElement rows = response.RootElement.GetProperty("result")[0].GetProperty("results");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1)
            throw new InvalidDataException("Invalid D1 snapshot query result.");
        return rows.GetArrayLength() == 0 ? null : rows[0].GetProperty("value").GetString() ??
            throw new InvalidDataException("D1 returned a null snapshot.");
    }

    protected override async ValueTask WriteSnapshot(string json, CancellationToken cancellationToken)
    {
        // D1 limits an individual row to 2 MB. Leave space for the logical database name and row overhead.
        if ((long)Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(_name) > 1_900_000)
            throw new InvalidOperationException("The D1 Librarian snapshot exceeds the supported 1,900,000-byte size. Use R2 for larger snapshots.");
        using JsonDocument response = await Query(
            "INSERT INTO librarian_snapshots (name, value) VALUES (?, ?) ON CONFLICT(name) DO UPDATE SET value = excluded.value",
            [_name, json], cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonDocument> Query(string sql, List<string> parameters, CancellationToken token)
    {
        var client = await _clientUtil.Get(_apiKey, token).ConfigureAwait(false);
        var body = new D1BatchQuery { D1SingleQuery = new D1SingleQuery { Sql = sql, Params = parameters } };
        // The generated D1 envelope models result as an object, but the API returns an array.
        // Keep the Soenneker client/authentication/serialization and consume the response stream directly.
        var handler = new NativeResponseHandler();
        await client.Accounts[_accountId].D1.Database[_databaseId].Query.PostAsync(body,
            config => config.Options.Add(new ResponseHandlerOption { ResponseHandler = handler }), token).ConfigureAwait(false);
        using var httpResponse = handler.Value as HttpResponseMessage ?? throw new InvalidDataException("D1 returned an empty response.");
        httpResponse.EnsureSuccessStatusCode();
        await using Stream stream = await httpResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        JsonDocument response = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        try
        {
            JsonElement root = response.RootElement;
            if (!root.TryGetProperty("success", out JsonElement success) || success.ValueKind != JsonValueKind.True ||
                (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0) ||
                !root.TryGetProperty("result", out JsonElement results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != 1 ||
                !results[0].TryGetProperty("success", out JsonElement statementSuccess) || statementSuccess.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("D1 rejected the Librarian query or returned an invalid result.");
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
