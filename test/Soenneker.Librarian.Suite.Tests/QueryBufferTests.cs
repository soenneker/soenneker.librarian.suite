using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public class QueryBufferTests
{

    [Test]
    public async ValueTask Long_queries_and_many_indexes_preserve_paging_closures_and_empty_intersections(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("wide", cancellationToken: cancellationToken);
        for (var i = 0; i < 40; i++)
            await container.AddItem(i.ToString(), $"{{\"a\":{i},\"b\":{i % 2},\"c\":1,\"d\":1,\"e\":1,\"f\":1,\"g\":1,\"h\":1,\"i\":1,\"j\":1}}", cancellationToken: cancellationToken);
        IQueryable<WideRow> root = container.BuildQueryable<WideRow>();
        var lower = 0;
        IQueryable<WideRow> wide = root.Where(row => row.A >= lower && row.B == 0 && row.C == 1 && row.D == 1 && row.E == 1
                                                     && row.F == 1 && row.G == 1 && row.H == 1 && row.I == 1 && row.J == 1);
        if (wide.Count() != 20) throw new Exception("Wide intersection failed.");
        lower = 20;
        if (wide.Count() != 10) throw new Exception("Closure value was cached.");
        IQueryable<WideRow> chain = wide.OrderBy(row => row.A);
        for (var i = 0; i < 12; i++) chain = chain.Skip(0).Take(40);
        if (!chain.Skip(2).Take(3).Select(row => row.A).SequenceEqual(new[] { 24, 26, 28 })) throw new Exception("Long plan failed.");
        IQueryable<int> projection = wide.OrderBy(row => row.A).Select(row => row.A);
        for (var i = 0; i < 12; i++) projection = projection.Skip(0).Take(40);
        if (!projection.Skip(2).Take(3).SequenceEqual(new[] { 24, 26, 28 })) throw new Exception("Long projected page failed.");
        // Each individual filter matches, but their intersection is empty. Exercise pooled-buffer return and count paths.
        for (var i = 0; i < 20; i++)
        {
            if (root.Where(row => row.A == 21 && row.B == 0).Take(4).ToArray().Length != 0) throw new Exception("Empty intersection matched.");
            if (wide.Skip(100).Any()) throw new Exception("Oversized skip matched.");
        }
        if (wide.Where(row => row.B == 0 && row.C == 1 && row.A % 4 == 0).Count() != 5)
            throw new Exception("Residual filter rollback failed.");
    }
}
