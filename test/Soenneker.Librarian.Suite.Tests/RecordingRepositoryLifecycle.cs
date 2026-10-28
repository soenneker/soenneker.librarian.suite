using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class RecordingRepositoryLifecycle : ILibrarianRepositoryLifecycle<ExampleDocument>
{
    internal ConcurrentQueue<string> Events { get; } = new();
    internal bool ChangeIdentity { get; set; }
    internal bool ThrowAfterUpdate { get; set; }
    internal bool DelayAdds { get; set; }
    internal int ActiveAdds;
    internal int PeakAdds;
    public async ValueTask BeforeAdd(ExampleDocument document, CancellationToken cancellationToken)
    {
        Events.Enqueue("before-add");
        if (ChangeIdentity) document.Id = "changed";
        if (DelayAdds)
        {
            int active = Interlocked.Increment(ref ActiveAdds);
            int peak;
            do { peak = PeakAdds; } while (active > peak && Interlocked.CompareExchange(ref PeakAdds, active, peak) != peak);
            await Task.Delay(20, cancellationToken);
        }
    }
    public ValueTask AfterAdd(ExampleDocument document) { if (DelayAdds) Interlocked.Decrement(ref ActiveAdds); Events.Enqueue("after-add"); return ValueTask.CompletedTask; }
    public ValueTask BeforeUpdate(ExampleDocument document, CancellationToken cancellationToken) { Events.Enqueue("before-update"); return ValueTask.CompletedTask; }
    public ValueTask AfterUpdate(ExampleDocument document)
    {
        Events.Enqueue("after-update");
        if (ThrowAfterUpdate) throw new InvalidOperationException("After hook failed after commit.");
        return ValueTask.CompletedTask;
    }
    public ValueTask BeforeDelete(ExampleDocument document, CancellationToken cancellationToken) { Events.Enqueue("before-delete"); return ValueTask.CompletedTask; }
    public ValueTask AfterDelete(ExampleDocument document) { Events.Enqueue("after-delete"); return ValueTask.CompletedTask; }
}
