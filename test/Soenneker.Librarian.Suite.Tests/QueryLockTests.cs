using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Asyncs.Locks;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Core;
using Soenneker.Librarian.Memory;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel]
public sealed class QueryLockTests
{
    [Test]
    public async Task Synchronous_queries_wait_for_mutations(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        using var gate = new AsyncLock();
        using var container = new LibrarianContainer("items", database, NullLogger.Instance, mutationGate: gate);
        await container.AddItem("one", "{\"score_value\":1}", cancellationToken: cancellationToken);
        IQueryable<QueryableRow> query = container.BuildQueryable<QueryableRow>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> result;
        using (await gate.Lock(cancellationToken: cancellationToken))
        {
            result = Task.Run(() =>
            {
                started.SetResult();
                return query.Count(row => row.Score == 1);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
            if (result.IsCompleted) throw new Exception("Query completed while the mutation lock was held.");
        }
        if (await result.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: cancellationToken) != 1) throw new Exception("Query lost the stored item.");
    }

    [Test]
    public async Task Asynchronous_queries_cancel_while_waiting_for_mutations(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        using var gate = new AsyncLock();
        using var container = new LibrarianContainer("items", database, NullLogger.Instance, mutationGate: gate);
        IQueryable<QueryableRow> query = container.BuildQueryable<QueryableRow>();
        using var cancellation = new CancellationTokenSource();
        using (await gate.Lock(cancellationToken: cancellationToken))
        {
            Task<int> result = query.CountAsync(row => row.Score == 1, cancellation.Token).AsTask();
            if (result.IsCompleted) throw new Exception("Query bypassed the mutation lock.");
            await cancellation.CancelAsync();
            try
            {
                await result.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
                throw new Exception("Query ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }
}
