using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.Librarian.Suite.Tests;

public class FileProxy : DispatchProxy
{
    public IFileUtil Inner = null!;
    public int Writes;
    public Func<Stream, CancellationToken, ValueTask>? BeforeWrite;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IFileUtil.WriteAtomically) && args![1] is Func<Stream, CancellationToken, ValueTask> writer)
        {
            Writes++;
            Func<Stream, CancellationToken, ValueTask>? before = BeforeWrite;
            if (before != null)
                args[1] = (Func<Stream, CancellationToken, ValueTask>)(async (stream, token) =>
                {
                    await before(stream, token);
                    await writer(stream, token);
                });
        }
        try { return targetMethod.Invoke(Inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
