using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class R2ObjectHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    internal int Writes;
    internal bool LoseNextWriteResponse;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_gate)
        {
            string key = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'));
            if (request.RequestUri.Query.Length > 0)
            {
                string prefix = Uri.UnescapeDataString(request.RequestUri.Query[8..].Split('&')[0]);
                string[] keys = _objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { keys, cursor = (string?)null }), Encoding.UTF8, "application/json") };
            }
            _objects.TryGetValue(key, out byte[]? existing);
            string? etag = existing is null ? null : Tag(existing);
            if (request.Method == HttpMethod.Put)
            {
                Writes++;
                bool matches = request.Headers.IfNoneMatch.Any(t => t.Tag == "*")
                    ? existing is null : existing is not null && request.Headers.IfMatch.Any(t => t.ToString() == etag);
                if (!matches) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                _objects[key] = body!;
                if (LoseNextWriteResponse)
                {
                    LoseNextWriteResponse = false;
                    throw new HttpRequestException("Response lost after the write committed.");
                }
                var created = new HttpResponseMessage(HttpStatusCode.OK);
                created.Headers.ETag = EntityTagHeaderValue.Parse(Tag(body!));
                return created;
            }
            if (existing is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(existing) };
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag!);
            return response;
        }
    }

    private static string Tag(byte[] bytes) => "\"" + Convert.ToHexString(SHA256.HashData(bytes)) + "\"";
}
