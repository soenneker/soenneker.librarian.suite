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
    private const string CheckConditionsScript = """
        for i = 1, #KEYS do
            local value = redis.call('HGET', KEYS[i], 'json')
            if ARGV[i * 2 - 1] == '0' then
                if value ~= false then return 0 end
            elseif value ~= ARGV[i * 2] then
                return 0
            end
        end
        return 1
        """;

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
            if (batch.Writes.Count == 0)
            {
                var keys = new RedisKey[batch.Conditions.Count];
                var values = new RedisValue[keys.Length * 2];
                for (int i = 0; i < keys.Length; i++)
                {
                    LibrarianCondition condition = batch.Conditions[i];
                    keys[i] = ((RedisLibrarianContainer)_containers[condition.Container]).BatchDocument(condition.Id);
                    values[i * 2] = condition.ExpectedValue is null ? "0" : "1";
                    values[i * 2 + 1] = condition.ExpectedValue ?? "";
                }
                cancellationToken.ThrowIfCancellationRequested();
                return (long)await store.ScriptEvaluateAsync(CheckConditionsScript, keys, values).NoSync() == 1;
            }
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
