using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Cloudflare.R2;
using Soenneker.Cloudflare.R2.Abstract;
using Soenneker.Librarian.R2.Abstract;

namespace Soenneker.Librarian.R2;

public sealed class R2LibrarianHttpObjectStore(ICloudflareR2ObjectStore store) : IR2LibrarianObjectStore
{
    public R2LibrarianHttpObjectStore(HttpClient httpClient, Uri endpoint, string apiKey)
        : this(new CloudflareR2WorkerObjectStore(httpClient, endpoint, apiKey)) { }

    public async ValueTask<R2LibrarianObject?> Read(string key, int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        CloudflareR2Object? value = await store.Read(key, maxBytes, cancellationToken);
        return value is null ? null : new R2LibrarianObject(value.Content, value.ETag);
    }

    public ValueTask<string?> Write(string key, ReadOnlyMemory<byte> content, string? expectedETag = null, CancellationToken cancellationToken = default) =>
        store.Write(key, content, expectedETag, cancellationToken);

    public async ValueTask<R2LibrarianObjectPage> List(string prefix, string? cursor = null, CancellationToken cancellationToken = default)
    {
        CloudflareR2ObjectPage page = await store.List(prefix, cursor, cancellationToken);
        return new R2LibrarianObjectPage(page.Keys, page.Cursor);
    }
}
