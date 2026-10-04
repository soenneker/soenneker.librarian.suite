using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Soenneker.Asyncs.Semaphores;
using Soenneker.Atomics.ValueBools;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Utils.HttpClientCache.Abstract;

namespace Soenneker.Librarian.CouchDb;

public sealed class CouchDbLibrarianDatabase : ILibrarianDatabase
{
    private readonly HttpClient? _client;
    private readonly IHttpClientCache? _httpClientCache;
    private readonly string _httpClientKey = "CouchDbLibrarianDatabase:" + Guid.NewGuid().ToString("N");
    private readonly CouchDbLibrarianOptions _options;
    private readonly Uri _endpoint;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly AsyncSemaphore _gate = new(1);
    private readonly Dictionary<(string Name, string? Partition), CouchDbLibrarianContainer> _containers = new();
    private readonly HashSet<string> _databases = new(StringComparer.Ordinal);
    private ValueAtomicBool _disposed = new(false);

    public CouchDbLibrarianDatabase(IConfiguration configuration, IHttpClientCache httpClientCache) : this(
        ReadOptions(configuration), httpClientCache)
    {
    }

    public CouchDbLibrarianDatabase(CouchDbLibrarianOptions options, IHttpClientCache httpClientCache) : this(options,
        null, httpClientCache ?? throw new ArgumentNullException(nameof(httpClientCache)))
    {
    }

    public CouchDbLibrarianDatabase(CouchDbLibrarianOptions options, HttpClient client) : this(options,
        client ?? throw new ArgumentNullException(nameof(client)), null)
    {
    }

    private CouchDbLibrarianDatabase(CouchDbLibrarianOptions options, HttpClient? client,
        IHttpClientCache? httpClientCache)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("http" or "https") ||
            options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0 ||
            options.Endpoint.UserInfo.Length != 0)
            throw new ArgumentException(
                "Endpoint must be an absolute HTTP(S) URL without credentials, query, or fragment.", nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Key);
        string prefix = options.DatabasePrefix;
        if (string.IsNullOrEmpty(prefix) || prefix[0] is < 'a' or > 'z' || prefix.Any(c =>
                c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-'))
            throw new ArgumentException(
                "DatabasePrefix must start with a lowercase letter and contain only lowercase ASCII letters, digits, or hyphens.",
                nameof(options));
        if ((options.Username is null) != (options.Password is null) ||
            options.Username?.Contains(':', StringComparison.Ordinal) == true)
            throw new ArgumentException("Supply both Username and Password; Username cannot contain a colon.",
                nameof(options));
        _options = options;
        _client = client;
        _httpClientCache = httpClientCache;
        _endpoint = new Uri(options.Endpoint.AbsoluteUri.TrimEnd('/') + "/");
        if (options.Username is not null)
            _authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(options.Username + ":" + options.Password)));
    }

    private static CouchDbLibrarianOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        const string prefix = "Librarian:CouchDb:";
        return new CouchDbLibrarianOptions
        {
            Endpoint = new Uri(
                configuration[prefix + "Endpoint"] ??
                throw new InvalidOperationException("Missing configuration: Librarian:CouchDb:Endpoint"),
                UriKind.Absolute),
            Username = configuration[prefix + "Username"], Password = configuration[prefix + "Password"],
            Key = configuration[prefix + "Key"] ?? "librarian",
            DatabasePrefix = configuration[prefix + "DatabasePrefix"] ?? "librarian",
            EnsureDatabaseOnFirstUse = configuration[prefix + "EnsureDatabaseOnFirstUse"] is not { } ensure ||
                                       bool.Parse(ensure)
        };
    }

    internal void Check(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        token.ThrowIfCancellationRequested();
    }

    private string DatabaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string result = _options.DatabasePrefix + "-" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(_options.Key)) +
                        "-" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(name));
        if (result.Length > 238)
            throw new ArgumentException("The encoded physical database name exceeds CouchDB's 238-byte limit.",
                nameof(name));
        return result;
    }

    internal async ValueTask<HttpResponseMessage> Send(HttpMethod method, string path, JsonNode? body,
        CancellationToken token)
    {
        Check(token);
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        if (_authorization is not null)
            request.Headers.Authorization = _authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpClient client = _client ?? await _httpClientCache!.Get(_httpClientKey, token).NoSync();
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).NoSync();
    }

    internal static async ValueTask<JsonDocument> Read(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).NoSync());
    }

    public ValueTask<ILibrarianContainer> GetContainer(string containerName,
        CancellationToken cancellationToken = default) =>
        GetContainerCore(containerName, null, cancellationToken);

    public ValueTask<ILibrarianContainer> GetContainer(string containerName, string partitionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        return GetContainerCore(containerName, partitionKey, cancellationToken);
    }

    private async ValueTask<ILibrarianContainer> GetContainerCore(string name, string? partition,
        CancellationToken token)
    {
        Check(token);
        string physical = DatabaseName(name);
        using (await _gate.Acquire(token).NoSync())
        {
            Check(token);
            if (_containers.TryGetValue((name, partition), out CouchDbLibrarianContainer? existing))
                return existing;
            if (!_databases.Contains(physical))
            {
                using HttpResponseMessage head = await Send(HttpMethod.Head, physical, null, token).NoSync();
                if (head.StatusCode == HttpStatusCode.NotFound && _options.EnsureDatabaseOnFirstUse)
                {
                    using HttpResponseMessage create = await Send(HttpMethod.Put, physical, null, token).NoSync();
                    // Another process may have provisioned the database between HEAD and PUT.
                    if (create.StatusCode != HttpStatusCode.PreconditionFailed)
                        create.EnsureSuccessStatusCode();
                    using HttpResponseMessage verify = await Send(HttpMethod.Head, physical, null, token).NoSync();
                    verify.EnsureSuccessStatusCode();
                }
                else
                    head.EnsureSuccessStatusCode();

                _databases.Add(physical);
            }

            var container = new CouchDbLibrarianContainer(this, physical, partition);
            _containers.Add((name, partition), container);
            return container;
        }
    }

    public ValueTask Save(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(batch);
        throw new NotSupportedException(
            "CouchDB does not provide atomic multi-document batches. Use revision-checked single-document writes.");
    }

    public ValueTask<bool> Execute(LibrarianBatch batch, string partitionKey,
        CancellationToken cancellationToken = default) => Execute(batch, cancellationToken);

    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        string physical = DatabaseName(containerName);
        using (await _gate.Acquire(cancellationToken).NoSync())
        {
            Check(cancellationToken);
            var keys = _containers.Keys.Where(key => key.Name == containerName).ToArray();
            foreach (var key in keys)
            {
                _containers[key].Dispose();
                _containers.Remove(key);
            }

            _databases.Remove(physical);
            return keys.Length != 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (await _gate.Acquire().NoSync())
        {
            if (!_disposed.TrySetTrue())
                return;
            foreach (CouchDbLibrarianContainer container in _containers.Values)
                container.Dispose();
            _containers.Clear();
            _databases.Clear();
            if (_httpClientCache is not null)
                await _httpClientCache.Remove(_httpClientKey).NoSync();
        }
    }
}