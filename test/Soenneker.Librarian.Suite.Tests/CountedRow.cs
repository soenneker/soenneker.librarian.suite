using System;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CountedRow
{
    public static int Created;
    public CountedRow() => Interlocked.Increment(ref Created);
    public string Status { get; set; } = "";
    public DateTime Date { get; set; }
}
