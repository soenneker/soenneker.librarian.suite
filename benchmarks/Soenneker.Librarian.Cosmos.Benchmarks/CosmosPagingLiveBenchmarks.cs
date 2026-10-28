using System.Diagnostics;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Cosmos;

internal static class CosmosPagingLiveBenchmarks
{
    internal static async Task Run(Container sdk, ICosmosLibrarianContainer items)
    {
        TransactionalBatch batch = sdk.CreateTransactionalBatch(new PartitionKey("paging"));
        for (int i = 0; i < 100; i++)
            batch.CreateItem(new CosmosBenchmarkDocument { DocumentId = i.ToString(), PartitionKey = "paging", Score = i });
        using TransactionalBatchResponse response = await batch.ExecuteAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(response.ErrorMessage);
        var query = new QueryDefinition("SELECT VALUE c.score FROM c WHERE c.partitionKey = @partition ORDER BY c.score")
            .WithParameter("@partition", "paging");
        async Task<int> Before()
        {
            int sum = 0;
            string? token = null;
            do
            {
                var page = await items.QueryPage<int>(query, 10, token);
                foreach (int value in page.Items) sum += value;
                token = page.ContinuationToken;
            } while (token is not null);
            return sum;
        }
        async Task<int> After()
        {
            int sum = 0;
            await items.QueryPages<int>(query, 10, page =>
            {
                foreach (int value in page) sum += value;
                return ValueTask.CompletedTask;
            });
            return sum;
        }
        Func<Task<int>>[] actions = [Before, After];
        foreach (var action in actions)
            for (int i = 0; i < 3; i++)
                if (await action() != 4950) throw new InvalidOperationException("Paging mismatch.");
        var times = new double[2, 5];
        var bytes = new double[2, 5];
        for (int round = 0; round < 5; round++)
            for (int turn = 0; turn < 2; turn++)
            {
                int index = (round + turn) % 2;
                long allocated = GC.GetTotalAllocatedBytes(true);
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 10; i++)
                    if (await actions[index]() != 4950) throw new InvalidOperationException("Paging mismatch.");
                times[index, round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / 10;
                bytes[index, round] = (GC.GetTotalAllocatedBytes(true) - allocated) / 10.0;
            }
        Console.WriteLine("Live gateway paging: 100 rows, 10 rows/page; median of 5 alternating rounds, 10 drains/round.");
        Console.WriteLine("Path,MicrosecondsPerDrain,ProcessBytesPerDrain");
        for (int index = 0; index < 2; index++)
            Console.WriteLine($"{(index == 0 ? "RebuildPerPage" : "SingleIterator")},{Enumerable.Range(0, 5).Select(r => times[index, r]).Order().ElementAt(2):F1},{Enumerable.Range(0, 5).Select(r => bytes[index, r]).Order().ElementAt(2):F0}");
    }
}
