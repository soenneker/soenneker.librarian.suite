using System.Net;
using System.Text;
using System.Text.Json;

namespace Soenneker.Librarian.Remote.Tests;

internal sealed class TurbopufferTestHandler : HttpMessageHandler
{
    internal Func<JsonElement, string> Respond { get; set; } = _ => "{\"rows\":[]}";
    internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    internal List<(string Path, JsonElement Body)> Requests { get; } = [];
    internal bool Disposed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        JsonElement value = body.RootElement.Clone();
        Requests.Add((request.RequestUri!.AbsolutePath, value));
        return new HttpResponseMessage(Status)
        {
            Content = new StringContent(Respond(value), Encoding.UTF8, "application/json")
        };
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
