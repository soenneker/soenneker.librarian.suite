using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions;
using Soenneker.Cloudflare.OpenApiClient;

namespace Soenneker.Librarian.Suite.Tests;

public class CloudflareClientProxy : DispatchProxy
{
    public CloudflareOpenApiClient Client = null!;
    public IRequestAdapter Adapter = null!;
    public string? LastApiKey;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name is not ("Get" or "GetRequestAdapter")) throw new NotSupportedException(targetMethod.Name);
        LastApiKey = args![0] as string;
        ((CancellationToken)args[^1]!).ThrowIfCancellationRequested();
        return targetMethod.Name == "GetRequestAdapter" ? ValueTask.FromResult(Adapter) : (object)ValueTask.FromResult(Client);
    }
}
