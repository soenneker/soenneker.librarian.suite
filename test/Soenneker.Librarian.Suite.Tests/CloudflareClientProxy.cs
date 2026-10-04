using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Cloudflare.OpenApiClient;

namespace Soenneker.Librarian.Suite.Tests;

public class CloudflareClientProxy : DispatchProxy
{
    public CloudflareOpenApiClient Client = null!;
    public string? LastApiKey;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name != "Get") throw new NotSupportedException(targetMethod.Name);
        LastApiKey = args![0] as string;
        ((CancellationToken)args[^1]!).ThrowIfCancellationRequested();
        return ValueTask.FromResult(Client);
    }
}
