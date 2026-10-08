using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel]
public class IncrementalBatchTests
{

    [Test]
    public async ValueTask Batch_updates_only_changed_automatic_index_entries_and_preserves_no_op_indexes(CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture("memory");
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        for (var i = 0; i < 100; i++) await items.AddItem(i.ToString(), "{\"score\":1}", cancellationToken: cancellationToken);
        await items.EnsureIndex("score", cancellationToken: cancellationToken);
        IQueryable<IncrementalBatchRow> query = items.BuildQueryable<IncrementalBatchRow>();
        Check(query.Count(row => row.Score == 1) == 100);
        IncrementalBatchRow.Created = 0;
        await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "0", "{\"score\":2}"), new LibrarianWrite("items", "1", null)]), cancellationToken: cancellationToken);
        Check(IncrementalBatchRow.Created == 1 && query.Count(row => row.Score == 1) == 98 && IncrementalBatchRow.Created == 1);
        Check(await items.CountRangeByIndex("score", 2, 2, cancellationToken: cancellationToken) == 1);
        await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "0", "{\"score\":2}"), new LibrarianWrite("items", "absent", null)]), cancellationToken: cancellationToken);
        Check(query.Count(row => row.Score == 2) == 1 && IncrementalBatchRow.Created == 1);
    }

    [Test]
    public async ValueTask Failed_filesystem_persistence_preserves_both_explicit_and_automatic_indexes(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("id", "{\"score\":1}", cancellationToken: cancellationToken);
        await items.EnsureIndex("score", cancellationToken: cancellationToken);
        IQueryable<IncrementalBatchRow> query = items.BuildQueryable<IncrementalBatchRow>();
        Check(query.Count(row => row.Score == 1) == 1);
        await fixture.Database.Save(cancellationToken: cancellationToken);
        fixture.Files.BeforeWrite = (_, _) => throw new System.IO.IOException("Injected failure");
        try
        {
            await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "id", "{\"score\":2}")]), cancellationToken: cancellationToken);
            throw new Exception("Expected persistence failure.");
        }
        catch (System.IO.IOException) { }
        finally { fixture.Files.BeforeWrite = null; }
        Check(query.Count(row => row.Score == 1) == 1 && await items.CountByIndex("score", 1, cancellationToken: cancellationToken) == 1);
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Incremental batch indexes are incorrect or were rebuilt.");
    }
}
