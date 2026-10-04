using System.Net;
using System.Text;

internal sealed class CouchDbSmokeHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json = request.Method == HttpMethod.Head ? "{}"
            : request.RequestUri!.AbsolutePath.EndsWith("/_index", StringComparison.Ordinal) ? "{\"result\":\"created\"}"
            : request.RequestUri.AbsolutePath.EndsWith("/_find", StringComparison.Ordinal) ? "{\"docs\":[{\"_id\":\"d-61-61\",\"_rev\":\"1-a\",\"id\":\"a\",\"partitionKey\":\"a\",\"name\":\"generated\"}]}"
            : "{\"ok\":true,\"rev\":\"1-a\"}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
