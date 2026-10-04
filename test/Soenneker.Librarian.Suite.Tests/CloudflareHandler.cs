using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Utils.Json;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class CloudflareHandler : HttpMessageHandler
{
    public string? Snapshot;
    public bool FailWrites;
    public bool FailStatement;
    public bool DenyReads;
    public int Writes;
    public int SchemaAttempts;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DenyReads) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (request.RequestUri!.AbsolutePath.Contains("/d1/", StringComparison.Ordinal))
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string sql = body.RootElement.GetProperty("sql").GetString()!;
            if (sql.StartsWith("CREATE", StringComparison.Ordinal))
            {
                SchemaAttempts++;
                return FailStatement
                    ? Json("{\"success\":true,\"result\":[{\"success\":false,\"error\":\"failure\"}]}")
                    : Json("{\"success\":true,\"result\":[{\"success\":true,\"results\":[]}]}");
            }
            JsonElement parameters = body.RootElement.GetProperty("params");
            if (parameters[0].GetString() != "name'with-quotes" || sql.Contains("name'with-quotes", StringComparison.Ordinal))
                throw new Exception("D1 logical name was not parameterized.");
            if (sql.StartsWith("SELECT", StringComparison.Ordinal))
                return Json("{\"success\":true,\"result\":[{\"success\":true,\"results\":" +
                    (Snapshot is null ? "[]" : "[{\"value\":" + JsonUtil.Serialize(Snapshot) + "}]") + "}]}");
            if (FailWrites) return Json("{\"success\":false,\"errors\":[{\"message\":\"failure\"}]}");
            if (FailStatement) return Json("{\"success\":true,\"result\":[{\"success\":false,\"error\":\"failure\"}]}");
            Writes++;
            Snapshot = parameters[1].GetString();
            return Json("{\"success\":true,\"result\":[{\"success\":true,\"results\":[]}]}");
        }
        if (request.Method == HttpMethod.Get)
            return Snapshot is null ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") } : Json(Snapshot);
        if (request.Method != HttpMethod.Put) throw new Exception("Unexpected R2 method.");
        if (FailWrites) return Json("{\"success\":false,\"errors\":[{\"message\":\"failure\"}]}");
        if (request.RequestUri.AbsolutePath.Contains("/storage/kv/", StringComparison.Ordinal))
        {
            if (!request.RequestUri.AbsolutePath.EndsWith("/namespaces/namespace/bulk", StringComparison.Ordinal))
                throw new Exception("Incorrect KV namespace or endpoint.");
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.GetArrayLength() != 1) throw new Exception("Expected one snapshot.");
            JsonElement entry = body.RootElement[0];
            if (entry.GetProperty("key").GetString() is not ("folder/librarian.json" or "librarian.json") ||
                entry.GetProperty("base64").GetBoolean() || entry.TryGetProperty("expiration", out _) ||
                entry.TryGetProperty("expiration_ttl", out _))
                throw new Exception("Incorrect KV write settings.");
            Snapshot = entry.GetProperty("value").GetString();
            Writes++;
            return Json("{\"success\":true,\"errors\":[],\"result\":{}}");
        }
        if (request.Content!.Headers.ContentType?.MediaType != "application/json") throw new Exception("Incorrect R2 content type.");
        Writes++;
        Snapshot = await request.Content.ReadAsStringAsync(cancellationToken);
        return Json("{\"success\":true,\"errors\":[],\"result\":{}}");
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
