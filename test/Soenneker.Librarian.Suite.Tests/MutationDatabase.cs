using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class MutationDatabase : ILibrarianDatabase
{
    public int DirtyCount;
    public CancellationToken LastToken;
    public ValueTask MarkDirty(string name, CancellationToken cancellationToken = default)
    {
        DirtyCount++;
        LastToken = cancellationToken;
        return ValueTask.CompletedTask;
    }
    public ValueTask<ILibrarianContainer> GetContainer(string name, CancellationToken token = default) => throw new NotSupportedException();
    public ValueTask<bool> UnloadContainer(string name, CancellationToken token = default) => throw new NotSupportedException();
    public ValueTask Save(CancellationToken token = default) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
