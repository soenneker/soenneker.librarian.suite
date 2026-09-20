using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Memory;

namespace Soenneker.Librarian.Suite.Tests;

public class AotContractTests
{
    [Test]
    public async Task Missing_contracts_fail_before_scanning_and_raw_JSON_still_works()
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        var container = await database.GetContainer("contracts");
        await container.AddItem("one", "{\"score\":1}");
        if (await container.GetItem("one") != "{\"score\":1}") throw new Exception("Raw CRUD changed.");
        try { container.BuildQueryable<UnregisteredRow>(); throw new Exception("Missing contract accepted."); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Register")) { }
    }

    [Test]
    public void Same_contract_is_idempotent_but_conflicting_contracts_are_rejected()
    {
        LibrarianJson.Register(TestJsonContext.Default.QueryableRow);
        LibrarianJson.Register(TestJsonContext.Default.QueryableRow);
        var alternative = new TestJsonContext(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        try { LibrarianJson.Register(alternative.QueryableRow); throw new Exception("Conflicting contract accepted."); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already registered")) { }
    }

    [Test]
    public async Task Nullable_aggregates_defaults_casts_and_unsupported_comparers_are_explicit()
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        var container = await database.GetContainer("values");
        await container.AddItem("one", "1");
        await container.AddItem("two", "2");
        IQueryable<int> root = container.BuildQueryable<int>();
        if (root.Sum() != 3 || root.Average() != 1.5 || root.Min() != 1 || root.Max() != 2) throw new Exception("Numeric aggregate changed.");
        IQueryable<int?> empty = root.Where(value => value > 10).Select(value => (int?)value);
        if (empty.Sum() != 0 || empty.Average() is not null || empty.Min() is not null || empty.Max() is not null) throw new Exception("Nullable aggregate changed.");
        if (root.Where(value => value > 10).FirstOrDefault(42) != 42) throw new Exception("Explicit default changed.");
        try { root.Min(System.Collections.Generic.Comparer<int>.Create((left, right) => right.CompareTo(left))); throw new Exception("Custom comparer silently ignored."); }
        catch (NotSupportedException) { }
        try { root.Select(value => (object?)null).Cast<int>().Count(); throw new Exception("Cast did not unbox null."); }
        catch (NullReferenceException) { }
    }

    public sealed class UnregisteredRow { public int Score { get; set; } }
}
