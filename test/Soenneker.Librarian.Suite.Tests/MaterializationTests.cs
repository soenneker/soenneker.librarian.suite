using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class MaterializationTests
{
    [Test]
    public async Task Materialized_results_do_not_alias_provider_collections(CancellationToken cancellationToken)
    {
        var source = new List<int> { 1, 2, 3 };
        var query = new LibrarianQueryable<int>(new MaterializationProvider<int>(() => source));
        List<int> list = await query.ToListAsync(cancellationToken: cancellationToken);
        int[] array = await query.ToArrayAsync(cancellationToken: cancellationToken);
        source[0] = 99;
        if (list[0] != 1 || array[0] != 1 || list.Capacity != 3) throw new Exception("Results alias the provider or overallocate.");
        var arrayQuery = new LibrarianQueryable<int>(new MaterializationProvider<int>(() => array));
        int[] copy = await arrayQuery.ToArrayAsync(cancellationToken: cancellationToken);
        array[0] = 88;
        if (copy[0] != 1) throw new Exception("Array result aliases the provider.");
    }

    [Test]
    public async Task Array_materialization_stops_and_disposes_a_cancelled_deferred_source(CancellationToken cancellationToken)
    {
        using var cancellation = new CancellationTokenSource();
        bool disposed = false;
        int visited = 0;
        IEnumerable<int> Source()
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    visited++;
                    if (i == 2) cancellation.Cancel();
                    yield return i;
                }
            }
            finally { disposed = true; }
        }
        var query = new LibrarianQueryable<int>(new MaterializationProvider<int>(Source));
        try { await query.ToArrayAsync(cancellation.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        if (!disposed || visited != 3) throw new Exception("Enumeration continued or was not disposed.");
    }
}
