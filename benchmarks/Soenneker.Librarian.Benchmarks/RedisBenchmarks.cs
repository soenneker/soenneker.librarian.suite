using System.Diagnostics;
using System.Reflection;
using Soenneker.Librarian.Redis;
using StackExchange.Redis;

internal static class RedisBenchmarks
{
    internal static void Encoding()
    {
        Type type = typeof(RedisLibrarianDatabase).Assembly.GetType("Soenneker.Librarian.Redis.RedisIndexValue")!;
        var encode = type.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic, [typeof(object)])!.CreateDelegate<Func<object?, string>>();
        var segment = type.GetMethod("KeySegment", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<string, string>>();
        // Prebox keys so this measures encoding, excluding the public object-parameter call site's boxing.
        object[] values = [decimal.MinValue, -1.2345678901234567890123456789m, 0m, 1m, decimal.MaxValue];
        Console.WriteLine("Operation,MedianMicroseconds,BytesPerOperation");
        AuditBenchmarks.Measure("Redis-decimal-key", i => encode(values[i % values.Length]).Length, 100000);
        AuditBenchmarks.Measure("Redis-safe-key-segment", _ => segment("flywheel.jobs").Length, 100000);
    }

    internal static async Task Run()
    {
        string connectionString = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_REDIS")
            ?? throw new InvalidOperationException("Set LIBRARIAN_TEST_REDIS to an existing test Redis server.");
        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        IDatabase store = connection.GetDatabase();
        string name = "librarian-benchmark-" + Guid.NewGuid().ToString("N");
        await using var database = new RedisLibrarianDatabase(name, _ => ValueTask.FromResult(store));
        try
        {
            var container = await database.GetContainer("rows");
            for (var i = 0; i < 100; i++)
                await container.AddItem(i.ToString(), $"{{\"score\":{i},\"group\":\"same\",\"payload\":\"0\"}}");
            var root = container.BuildQueryable<Row>();
            if (root.Count(row => row.Score == 50) != 1 || root.Count(row => row.Group == "same") != 100)
                throw new Exception("Redis benchmark result mismatch.");
            // Warm schemas before measuring calls; mutations change only an unindexed payload.
            string[] json = ["{\"score\":50,\"group\":\"same\",\"payload\":\"0\"}", "{\"score\":50,\"group\":\"same\",\"payload\":\"1\"}"];
            Console.WriteLine("Operation,MedianMicroseconds,ProcessBytesPerOperation,CommandsPerOperation");
            await Measure("Redis-equality-count", _ => ValueTask.FromResult(root.Count(row => row.Score == 50)), connection);
            await Measure("Redis-equality-page-10", _ => ValueTask.FromResult(root.Where(row => row.Group == "same").Take(10).ToArray().Length), connection);
            await Measure("Redis-update-unchanged-index-values", async i => (await container.UpdateItemStrict("50", json[i % 2])).Length, connection);
            if (root.Count(row => row.Score == 50) != 1 || root.Count(row => row.Group == "same") != 100)
                throw new Exception("Redis benchmark indexes changed.");
        }
        finally
        {
            // Delete only the unique namespace created by this invocation; the server is caller-owned.
            foreach (var endpoint in connection.GetEndPoints())
                await foreach (RedisKey key in connection.GetServer(endpoint).KeysAsync(pattern: "librarian:{" + name + "}:containers:*"))
                    await store.KeyDeleteAsync(key);
        }
    }

    private static async Task Measure(string name, Func<int, ValueTask<int>> action, ConnectionMultiplexer connection)
    {
        const int iterations = 200;
        for (var i = 0; i < 30; i++) await action(i);
        double[] times = new double[5], bytes = new double[5], commands = new double[5];
        long checksum = 0;
        for (var round = 0; round < 5; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetTotalAllocatedBytes(true);
            long operations = connection.OperationCount;
            long start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++) checksum += await action(i);
            times[round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
            commands[round] = (connection.OperationCount - operations) / (double)iterations;
            bytes[round] = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)iterations;
        }
        Array.Sort(times); Array.Sort(bytes); Array.Sort(commands);
        Console.WriteLine($"{name},{times[2]:F3},{bytes[2]:F0},{commands[2]:F2}");
        GC.KeepAlive(checksum);
    }
}
