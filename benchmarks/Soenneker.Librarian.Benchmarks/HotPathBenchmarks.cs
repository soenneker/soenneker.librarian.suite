using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Memory;

internal static class HotPathBenchmarks
{
    internal static async Task Run()
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        var container = await database.GetContainer("hotpaths");
        const int size = 10000;
        string[] ids = Enumerable.Range(0, size).Select(i => $"id-{i:D8}").ToArray();
        string[] json = Enumerable.Range(0, size).Select(i => $"{{\"score\":{i},\"group\":\"group-{i % 10}\",\"payload\":\"data\"}}").ToArray();
        for (int i = 0; i < size; i++) await container.AddItem(ids[i], json[i]);
        Console.WriteLine("Operation,MedianMicroseconds,BytesPerOperation");
        AuditBenchmarks.Measure("GetItem", i => container.GetItem(ids[i % size]).GetAwaiter().GetResult()!.Length, 100000);
        AuditBenchmarks.Measure("Update-unindexed", i => container.UpdateItemStrict(ids[i % size], json[(i + i / size + 1) % size]).GetAwaiter().GetResult().Length, 100000);
        AuditBenchmarks.Measure("GetAllItems-10000", _ => container.GetAllItems().GetAwaiter().GetResult().Count, 100);
        AuditBenchmarks.Measure("GetAllIds-10000", _ => container.GetAllIds().GetAwaiter().GetResult().Count, 100);
        AuditBenchmarks.Measure("Snapshot-10000", _ => container.GetLibrarianItems().GetAwaiter().GetResult().Count, 100);
        var root = container.BuildQueryable<Row>();
        root.Count(row => row.Score >= 0);
        root.Count(row => row.Group == "group-1");
        AuditBenchmarks.Measure("Update-two-automatic-indexes", i => container.UpdateItemStrict(ids[i % size], json[(i + i / size + 1) % size]).GetAwaiter().GetResult().Length, 20000);
        await container.EnsureIndex("score");
        await container.EnsureIndex("group");
        await container.EnsureIndex("payload");
        var batches = new[] { new LibrarianBatch([new("hotpaths", ids[0], json[0])]), new LibrarianBatch([new("hotpaths", ids[0], json[1])]) };
        AuditBenchmarks.Measure("Batch-10000-three-explicit-indexes", i => database.Execute(batches[i % 2]).GetAwaiter().GetResult() ? 1 : 0, 100);
        // Alternate values to measure real writes; the numeric index should survive each batch.
        root.Count(row => row.Score >= 0);
        AuditBenchmarks.Measure("Batch-and-numeric-count-10000", i =>
        {
            database.Execute(batches[i % 2]).GetAwaiter().GetResult();
            return root.Count(row => row.Score >= 0);
        }, 100);
    }
}
