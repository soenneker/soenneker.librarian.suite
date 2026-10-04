using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class IncrementalBatchRow
{
    public static int Created;
    public IncrementalBatchRow() => Interlocked.Increment(ref Created);
    public int Score { get; set; }
}
