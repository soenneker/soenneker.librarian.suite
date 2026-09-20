using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Memory;

Soenneker.Librarian.Abstractions.Serialization.LibrarianJson.Register(BenchmarkJsonContext.Default.Row);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Contains("--hotpaths")) { await HotPathBenchmarks.Run(); return; }
if (args.Contains("--encoding")) { RedisBenchmarks.Encoding(); return; }
if (args.Contains("--redis")) { await RedisBenchmarks.Run(); return; }
const int size = 100_000;
Console.WriteLine($"# {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; CPUs={Environment.ProcessorCount}; documents={size}; tiering disabled");
await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
var container = await database.GetContainer("rows");
using var stream = new MemoryStream();
using var lite = new LiteDatabase(stream);
var collection = lite.GetCollection<Row>("rows");
var rows = Enumerable.Range(0, size).Select(i => new Row
{
    Id = $"id-{i:D8}", Group = $"group-{i / 10:D6}", Score = i, Payload = new string('x', 128)
}).ToArray();
foreach (Row row in rows) await container.AddItem(row.Id, System.Text.Json.JsonSerializer.Serialize(row));
collection.InsertBulk(rows);
collection.EnsureIndex(row => row.Group);
collection.EnsureIndex(row => row.Score);
using var sqlite = new SQLite.SQLiteConnection(":memory:");
sqlite.CreateTable<Row>();
sqlite.InsertAll(rows);
sqlite.CreateIndex<Row>(row => row.Group);
sqlite.CreateIndex<Row>(row => row.Score);
if (args.Contains("--plans"))
{
    Console.WriteLine("Equality: " + collection.Query().Where(row => row.Group == "group-000001").Limit(10).GetPlan().ToString());
    Console.WriteLine("Range: " + collection.Query().Where(row => row.Score >= 100 && row.Score <= 150).OrderBy(row => row.Score).Offset(5).Limit(10).GetPlan().ToString());
    Console.WriteLine("Deep: " + collection.Query().OrderBy(row => row.Score).Offset(90_000).Limit(10).GetPlan().ToString());
    return;
}

// Every invocation constructs and executes a fresh expression/query, including timed invocations.
List<Row> LibrarianEquality(int i) { string group = rows[i % 100 * 1000].Group; return container.BuildQueryable<Row>().Where(row => row.Group == group).Take(10).ToList(); }
List<Row> LiteEquality(int i) { string group = rows[i % 100 * 1000].Group; return collection.Query().Where(row => row.Group == group).Limit(10).ToList(); }
List<Row> LibrarianRange(int i) { int min = i % 100 * 990; int max = min + 50; return container.BuildQueryable<Row>().Where(row => row.Score >= min && row.Score <= max).OrderBy(row => row.Score).Skip(5).Take(10).ToList(); }
List<Row> LiteRange(int i) { int min = i % 100 * 990; int max = min + 50; return collection.Query().Where(row => row.Score >= min && row.Score <= max).OrderBy(row => row.Score).Offset(5).Limit(10).ToList(); }
List<Row> LibrarianDeep(int i) => container.BuildQueryable<Row>().OrderBy(row => row.Score).Skip(90_000).Take(10).ToList();
List<Row> LiteDeep(int i) => collection.Query().OrderBy(row => row.Score).Offset(90_000).Limit(10).ToList();
int LibrarianCount(int i) { string group = rows[i % 100 * 1000].Group; return container.BuildQueryable<Row>().Count(row => row.Group == group); }
int LiteCount(int i) { string group = rows[i % 100 * 1000].Group; return collection.Count(row => row.Group == group); }
bool LibrarianExists(int i) { string group = i % 2 == 0 ? rows[i % 100 * 1000].Group : "absent"; return container.BuildQueryable<Row>().Any(row => row.Group == group); }
bool LiteExists(int i) { string group = i % 2 == 0 ? rows[i % 100 * 1000].Group : "absent"; return collection.Exists(row => row.Group == group); }

List<Row> SqliteEquality(int i) { string group = rows[i % 100 * 1000].Group; return sqlite.Table<Row>().Where(row => row.Group == group).Take(10).ToList(); }
List<Row> SqliteRange(int i) { int min = i % 100 * 990; int max = min + 50; return sqlite.Table<Row>().Where(row => row.Score >= min && row.Score <= max).OrderBy(row => row.Score).Skip(5).Take(10).ToList(); }
List<Row> SqliteDeep(int i) => sqlite.Table<Row>().OrderBy(row => row.Score).Skip(90_000).Take(10).ToList();
int SqliteCount(int i) { string group = rows[i % 100 * 1000].Group; return sqlite.Table<Row>().Count(row => row.Group == group); }
bool SqliteExists(int i) { string group = i % 2 == 0 ? rows[i % 100 * 1000].Group : "absent"; return sqlite.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM \"Row\" WHERE \"Group\" = ?)", group) != 0; }
// Check IDs against an independent model, not just agreement between engines.
for (int i = 0; i < 100; i++)
{
    int first = i * 1000;
    if (container.BuildQueryable<Row>().First(row => row.Score == first).Id != rows[first].Id
        || collection.Query().Where(row => row.Score == first).First().Id != rows[first].Id) throw new Exception("First mismatch");
    if (sqlite.Table<Row>().Where(row => row.Score == first).First().Id != rows[first].Id) throw new Exception("sqlite-net First mismatch");
    Validate(LibrarianEquality(i), rows.Skip(first).Take(10), false);
    Validate(SqliteEquality(i), rows.Skip(first).Take(10), false);
    Validate(LiteEquality(i), rows.Skip(first).Take(10), false);
    Validate(LibrarianRange(i), rows.Skip(i * 990 + 5).Take(10), true);
    Validate(SqliteRange(i), rows.Skip(i * 990 + 5).Take(10), true);
    Validate(LiteRange(i), rows.Skip(i * 990 + 5).Take(10), true);
    if (SqliteCount(i) != 10 || SqliteExists(i) != (i % 2 == 0) || LibrarianCount(i) != 10 || LiteCount(i) != 10 || LibrarianExists(i) != (i % 2 == 0) || LiteExists(i) != (i % 2 == 0)) throw new Exception("Scalar mismatch");
}
Validate(LibrarianDeep(0), rows.Skip(90_000).Take(10), true);
Validate(SqliteDeep(0), rows.Skip(90_000).Take(10), true);
Validate(LiteDeep(0), rows.Skip(90_000).Take(10), true);
Console.WriteLine("# All results validated; all engines and their indexes warm before timing.");
if (args.Contains("--validate")) return;
if (args.Contains("--planner")) { PlannerBenchmarks.Run(container); return; }
if (args.Contains("--audit")) { AuditBenchmarks.Run(container); return; }
Console.WriteLine("Query,Engine,MedianMicroseconds,BytesPerOperation,IterationsPerRound,Rounds");
Measure("Equality-10", i => LibrarianEquality(i).Count, i => LiteEquality(i).Count, i => SqliteEquality(i).Count);
Measure("Range-page-10", i => LibrarianRange(i).Count, i => LiteRange(i).Count, i => SqliteRange(i).Count);
Measure("Count-10", LibrarianCount, LiteCount, SqliteCount);
Measure("Exists-50%-hits", i => LibrarianExists(i) ? 1 : 0, i => LiteExists(i) ? 1 : 0, i => SqliteExists(i) ? 1 : 0);
Measure("First-equality", i => { int score = i % 100 * 990; return container.BuildQueryable<Row>().First(row => row.Score == score).Score; },
    i => { int score = i % 100 * 990; return collection.Query().Where(row => row.Score == score).First().Score; },
    i => { int score = i % 100 * 990; return sqlite.Table<Row>().Where(row => row.Score == score).First().Score; });
Measure("Deep-page-90000-10", i => LibrarianDeep(i).Count, i => LiteDeep(i).Count, i => SqliteDeep(i).Count, 100);

static void Validate(IEnumerable<Row> actual, IEnumerable<Row> expected, bool ordered)
{
    IEnumerable<string> a = actual.Select(row => row.Id), b = expected.Select(row => row.Id);
    if (!ordered) { a = a.Order(); b = b.Order(); }
    if (!a.SequenceEqual(b)) throw new Exception("Document ID mismatch");
}

static void Measure(string name, Func<int, int> librarian, Func<int, int> lite, Func<int, int> sqlite, int iterations = 2000)
{
    const int rounds = 7;
    var times = new double[3, rounds];
    var bytes = new double[3, rounds];
    Func<int, int>[] actions = [librarian, lite, sqlite];
    long checksum = 0;
    foreach (var action in actions) for (int i = 0; i < 200; i++) checksum += action(i);
    for (int round = 0; round < rounds; round++)
    {
        // Rotate engines to reduce run-order bias.
        for (int turn = 0; turn < 3; turn++)
        {
            int engine = (round + turn) % 3;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) checksum += actions[engine](i);
            times[engine, round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
            bytes[engine, round] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)iterations;
        }
    }
    for (int engine = 0; engine < 3; engine++)
    {
        double time = Enumerable.Range(0, rounds).Select(round => times[engine, round]).Order().ElementAt(rounds / 2);
        double allocation = Enumerable.Range(0, rounds).Select(round => bytes[engine, round]).Order().ElementAt(rounds / 2);
        Console.WriteLine($"{name},{(engine == 0 ? "Librarian" : engine == 1 ? "LiteDB-5.0.21" : "sqlite-net-1.11.285")},{time:F3},{allocation:F0},{iterations},{rounds}");
    }
    GC.KeepAlive(checksum);
}


