using System;
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
        batch.ValidateConcurrency(supportsVersions: false);
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
                for (var i = 0; i < keys.Length; i++)
                {
                    LibrarianCondition condition = batch.Conditions[i];
                    keys[i] = ((RedisLibrarianContainer)_containers[condition.Container]).BatchDocument(condition.Id);
                    values[i * 2] = condition.ExpectedValue is null ? "0" : "1";
                    values[i * 2 + 1] = condition.ExpectedValue ?? "";
                }
                cancellationToken.ThrowIfCancellationRequested();
                return (long)await store.ScriptEvaluateAsync(CheckConditionsScript, keys, values).WaitAsync(cancellationToken).NoSync() == 1;
            }
            // Group once; optimistic retries reuse the immutable input groups.
            ILookup<string, LibrarianWrite> writesByContainer = batch.Writes.ToLookup(write => write.Container, StringComparer.Ordinal);
            ILookup<string, LibrarianCondition> conditionsByContainer = batch.Conditions.ToLookup(condition => condition.Container, StringComparer.Ordinal);
            var writes = new LibrarianWrite[names.Length][];
            var conditions = new LibrarianCondition[names.Length][];
            for (var i = 0; i < names.Length; i++)
            {
                writes[i] = writesByContainer[names[i]].ToArray();
                conditions[i] = conditionsByContainer[names[i]].ToArray();
            }
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var commands = new RedisBatchCommands();
                for (var i = 0; i < names.Length; i++)
                {
                    var container = (RedisLibrarianContainer)_containers[names[i]];
                    if (!await container.PrepareBatch(store, conditions[i], writes[i], commands, cancellationToken).NoSync()) return false;
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Observe the commit outcome even if cancellation arrives after submission.
                if ((long)await commands.Execute(store).NoSync() == 1) return true;
                if (attempt >= 127) throw new TimeoutException("The Librarian batch repeatedly conflicted with concurrent writes.");
                await Task.Delay(Random.Shared.Next(1, 10), cancellationToken).NoSync();
            }
        }
    }
}
