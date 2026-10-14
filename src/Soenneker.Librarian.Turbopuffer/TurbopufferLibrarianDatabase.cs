using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Soenneker.Librarian.Abstractions;
using Turbopuffer;

namespace Soenneker.Librarian.Turbopuffer;

public sealed class TurbopufferLibrarianDatabase : ILibrarianDatabase
{
    private readonly ITurbopufferClient _client;
    private readonly bool _ownsClient;
    private readonly string _key;
    private readonly Dictionary<string, TurbopufferLibrarianContainer> _containers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public TurbopufferLibrarianDatabase(IConfiguration configuration)
        : this(CreateClient(configuration), configuration["Librarian:Turbopuffer:Key"] ?? "librarian", true) { }

    public TurbopufferLibrarianDatabase(ITurbopufferClient client, string key = "librarian") : this(client, key, false) { }

    private TurbopufferLibrarianDatabase(ITurbopufferClient client, string key, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _client = client;
        _key = key;
        _ownsClient = ownsClient;
    }

    private static TurbopufferClient CreateClient(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string apiKey = configuration["Librarian:Turbopuffer:ApiKey"] ?? throw new InvalidOperationException("Missing configuration: Librarian:Turbopuffer:ApiKey");
        string region = configuration["Librarian:Turbopuffer:Region"] ?? throw new InvalidOperationException("Missing configuration: Librarian:Turbopuffer:Region");
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration["Librarian:Turbopuffer:Key"] ?? "librarian");
        return new TurbopufferClient { ApiKey = apiKey, Region = region, MaxRetries = 0 };
    }

    public ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_containers.TryGetValue(containerName, out TurbopufferLibrarianContainer? container))
            {
                // Length-prefixing prevents ambiguous key/name pairs. Hashing accommodates arbitrary names within the namespace limit.
                string identity = _key.Length + ":" + _key + containerName;
                string name = "librarian-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                container = new TurbopufferLibrarianContainer(_client.Namespace(name).WithOptions(options => options with { MaxRetries = 0 }));
                _containers.Add(containerName, container);
            }
            return ValueTask.FromResult<ILibrarianContainer>(container);
        }
    }

    public ValueTask Save(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default) => Save(cancellationToken);

    public ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_containers.Remove(containerName, out TurbopufferLibrarianContainer? container)) return ValueTask.FromResult(false);
            container.Dispose();
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            foreach (TurbopufferLibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
            if (_ownsClient) ((TurbopufferClient)_client).Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
