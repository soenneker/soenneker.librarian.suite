using System.Diagnostics;
using Soenneker.Librarian.Abstractions;

internal static class AuditBenchmarks
{
    internal static void Run(ILibrarianContainer container)
    {
        var root = container.BuildQueryable<Row>();
        var reusable = root.Where(row => row.Score >= 50000 && row.Score < 50100).OrderBy(row => row.Score).Take(10).Select(row => row.Score);
        Console.WriteLine("Operation,MedianMicroseconds,BytesPerOperation");
        // Single cold samples are explicitly labeled; they are not medians or warm read comparisons.
        Cold("First-scan-snapshot-single-sample", () => root.Take(10).ToList().Count);
        string payload = new string('x', 128);
        Cold("First-Payload-index-single-sample", () => root.Count(row => row.Payload == payload));
        Row[] expected = root.ToArray();
        if (!reusable.SequenceEqual(Enumerable.Range(50000, 10))
            || !root.Take(10).Select(row => row.Id).SequenceEqual(expected.Take(10).Select(row => row.Id))
            || !root.Where(row => row.Score % 2 == 0).Take(10).Select(row => row.Id)
                .SequenceEqual(expected.Where(row => row.Score % 2 == 0).Take(10).Select(row => row.Id))
            || !root.Where(row => row.Score >= 50000).Where(row => row.Score < 50100).OrderBy(row => row.Score).Take(10).Select(row => row.Score)
                .SequenceEqual(Enumerable.Range(50000, 10))) throw new Exception("Audit benchmark result mismatch");
        Measure("BuildQueryable", _ => container.BuildQueryable<Row>().GetHashCode(), 100000);
        Measure("Compose-Where-OrderBy-Skip-Take", _ => root.Where(row => row.Score >= 50000).OrderBy(row => row.Score).Skip(5).Take(10).GetHashCode(), 10000);
        Measure("Indexed-page-Select-fresh", _ => root.Where(row => row.Score >= 50000 && row.Score < 50100).OrderBy(row => row.Score).Take(10).Select(row => row.Score).ToArray().Length, 200);
        Measure("Indexed-page-Select-reused", _ => reusable.ToArray().Length, 200);
        Measure("Unindexed-Take-10", _ => root.Take(10).ToList().Count, 30);
        Measure("Fallback-Where-Take-10", _ => root.Where(row => row.Score % 2 == 0).Take(10).ToList().Count, 30);
        Measure("Split-Where-page", _ => root.Where(row => row.Score >= 50000).Where(row => row.Score < 50100).OrderBy(row => row.Score).Take(10).ToList().Count, 30);
    }

    private static void Cold(string name, Func<int> action)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        int result = action();
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"{name},{elapsed:F3},{bytes}");
        GC.KeepAlive(result);
    }

    internal static void Measure(string name, Func<int,int> action, int iterations)
    {
        long checksum = 0;
        for (int i = 0; i < Math.Min(iterations, 30); i++) checksum += action(i);
        double[] times = new double[5], allocations = new double[5];
        for (int round = 0; round < 5; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) checksum += action(i);
            times[round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
            allocations[round] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)iterations;
        }
        Array.Sort(times); Array.Sort(allocations);
        Console.WriteLine($"{name},{times[2]:F3},{allocations[2]:F0}");
        GC.KeepAlive(checksum);
    }
}
