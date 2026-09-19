using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Transactions;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianDatabase
{
    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            IDatabase store = await GetStore(cancellationToken).NoSync();
            string[] names = batch.Writes.Select(write => write.Container).Concat(batch.Conditions.Select(condition => condition.Container))
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (string name in names)
                if (!_containers.ContainsKey(name)) _containers.Add(name, new RedisLibrarianContainer(name, this));
            if (names.Length == 0) return true;
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var actions = new List<Action<ITransaction, List<Task>>>();
                foreach (string name in names)
                {
                    var container = (RedisLibrarianContainer)_containers[name];
                    Action<ITransaction, List<Task>>? action = await container.PrepareBatch(store, batch.Conditions.Where(condition => condition.Container == name),
                        batch.Writes.Where(write => write.Container == name), cancellationToken).NoSync();
                    if (action is null) return false;
                    actions.Add(action);
                }
                cancellationToken.ThrowIfCancellationRequested();
                ITransaction transaction = store.CreateTransaction();
                var commands = new List<Task>();
                foreach (Action<ITransaction, List<Task>> action in actions) action(transaction, commands);
                bool committed = await transaction.ExecuteAsync().NoSync();
                try { await Task.WhenAll(commands).NoSync(); }
                catch (TaskCanceledException) when (!committed) { }
                if (committed) return true;
                if (attempt >= 127) throw new TimeoutException("The Librarian batch repeatedly conflicted with concurrent writes.");
                await Task.Delay(Random.Shared.Next(1, 10), cancellationToken).NoSync();
            }
        }
    }
}
