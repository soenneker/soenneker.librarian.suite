using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Asyncs.Locks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Core;

/// <summary>Coordinates in-memory container operations with prepared atomic batches.</summary>
public sealed class LibrarianBatchExecutor
{
    /// <summary>The gate supplied to every container owned by the database.</summary>
    public AsyncLock Gate { get; } = new();

    /// <summary>Validates and stages all changes, optionally persists their snapshots, then publishes them together.</summary>
    /// <remarks>The provider must prevent container creation, unloading and disposal during this call.
    /// The persistence callback must not call container operations because the shared gate is held.</remarks>
    public async ValueTask<bool> Execute(LibrarianBatch batch, IReadOnlyDictionary<string, LibrarianContainer> containers,
        Func<IReadOnlyDictionary<string, List<IdValuePair>>, CancellationToken, ValueTask>? persist = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using (await Gate.Lock(cancellationToken).NoSync())
        {
            foreach (LibrarianCondition condition in batch.Conditions)
                if (!string.Equals(containers[condition.Container].ReadForBatch(condition.Id), condition.ExpectedValue, StringComparison.Ordinal)) return false;
            var prepared = new Dictionary<string, LibrarianContainerState>(StringComparer.Ordinal);
            foreach (IGrouping<string, LibrarianWrite> group in batch.Writes.GroupBy(write => write.Container, StringComparer.Ordinal))
                prepared.Add(group.Key, containers[group.Key].PrepareBatch(group, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (persist is not null && prepared.Count > 0)
            {
                var snapshots = new Dictionary<string, List<IdValuePair>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, LibrarianContainer> pair in containers)
                    snapshots.Add(pair.Key, pair.Value.SnapshotForBatch(prepared.GetValueOrDefault(pair.Key)));
                await persist(snapshots, cancellationToken).NoSync();
                // Persistence is the commit point. Do not cancel publication after it succeeds.
            }
            foreach (KeyValuePair<string, LibrarianContainerState> pair in prepared) containers[pair.Key].PublishBatch(pair.Value);
            return true;
        }
    }
}
