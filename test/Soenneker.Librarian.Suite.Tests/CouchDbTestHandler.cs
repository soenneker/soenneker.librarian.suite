using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Atomics.ValueBools;
using Soenneker.Extensions.Task;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class CouchDbTestHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, string, HttpResponseMessage>> _responses = new();
    private int _calls;
    private ValueAtomicBool _disposed = new(false);
    public int Calls => _calls;
    public bool IsDisposed => _disposed.Value;
    public Func<CancellationToken, Task>? BeforeSend;

    public void Expect(HttpMethod method, string suffix, string json = "{}", HttpStatusCode status = HttpStatusCode.OK, Action<HttpRequestMessage, string>? inspect = null)
    {
        _responses.Enqueue((request, body) =>
        {
            if (request.Method != method || !request.RequestUri!.PathAndQuery.EndsWith(suffix, StringComparison.Ordinal))
                throw new Exception($"Expected {method} {suffix}, received {request.Method} {request.RequestUri!.PathAndQuery}.");
            inspect?.Invoke(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
    }

    public void Verify() { if (!_responses.IsEmpty) throw new Exception("Expected CouchDB requests were not sent."); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (BeforeSend is { } before) await before(cancellationToken).NoSync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_responses.TryDequeue(out var response)) throw new Exception("Unexpected CouchDB request: " + request.Method + " " + request.RequestUri);
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).NoSync();
        return response(request, body);
    }

    protected override void Dispose(bool disposing) { _disposed.TrySetTrue(); base.Dispose(disposing); }
}
