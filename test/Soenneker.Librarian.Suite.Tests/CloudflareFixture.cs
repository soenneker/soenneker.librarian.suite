using System;
using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Soenneker.Cloudflare.OpenApiClient;
using Soenneker.Cloudflare.R2;
using Soenneker.Cloudflare.D1;
using Soenneker.Cloudflare.Workers.Kv;
using Soenneker.Cloudflare.Utils.Client.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.D1;
using Soenneker.Librarian.Kv;
using Soenneker.Librarian.R2;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class CloudflareFixture : IDisposable
{
    private readonly string _provider;
    private readonly HttpClient _http;
    private readonly HttpClientRequestAdapter _adapter;
    private readonly ICloudflareClientUtil _clientUtil;
    public ICloudflareClientUtil ClientUtil => _clientUtil;
    public readonly CloudflareHandler Handler = new();
    public readonly CloudflareClientProxy Clients;

    public CloudflareFixture(string provider)
    {
        _provider = provider;
        _http = new HttpClient(Handler);
        _adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: _http);
        _clientUtil = DispatchProxy.Create<ICloudflareClientUtil, CloudflareClientProxy>();
        Clients = (CloudflareClientProxy)_clientUtil;
        Clients.Client = new CloudflareOpenApiClient(_adapter);
    }

    public ILibrarianDatabase Create() => _provider == "d1"
        ? new D1LibrarianDatabase("account", "test-token", "database", new CloudflareD1Util(_clientUtil), NullLogger.Instance, "name'with-quotes")
        : _provider == "kv"
        ? new KvLibrarianDatabase("account", "test-token", "namespace", new CloudflareWorkersKvUtil(_clientUtil, NullLogger<CloudflareWorkersKvUtil>.Instance), NullLogger.Instance, "folder/librarian.json")
        : new R2LibrarianDatabase("account", "bucket", "folder/librarian.json", new CloudflareR2Util(_clientUtil), NullLogger.Instance, "test-token");

    public void Dispose()
    {
        _adapter.Dispose();
        _http.Dispose();
    }
}
