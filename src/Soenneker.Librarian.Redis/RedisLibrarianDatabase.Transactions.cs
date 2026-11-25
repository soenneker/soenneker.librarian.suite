using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Transactions;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianDatabase
{
    private static readonly Meter Meter = new("Soenneker.Librarian.Redis");
    private static readonly Counter<long> BatchConflicts = Meter.CreateCounter<long>("librarian.redis.batch.conflicts");

    internal static void RecordConditionConflict(LibrarianCondition condition)
    {
        if (BatchConflicts.Enabled)
            BatchConflicts.Add(1, new KeyValuePair<string, object?>("container", condition.Container),
                new KeyValuePair<string, object?>("condition", condition.ExpectedValue is null ? "missing" : "value"));
    }

    private const string CheckConditionsScript = """
        for i = 1, #KEYS do
            local value = redis.call('HGET', KEYS[i], 'json')
            if ARGV[i * 2 - 1] == '0' then
                if value ~= false then return -i end
            elseif value ~= ARGV[i * 2] then
                return -i
            end
        end
        return 1
        """;

    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch.ValidateConcurrency(supportsVersions: false);
        using var slot = await _batchSlots.Acquire(cancellationToken).NoSync();
        string[] names = batch.Writes.Select(write => write.Container).Concat(batch.Conditions.Select(condition => condition.Container))
            .Distinct(StringComparer.Ordinal).ToArray();
        var containers = new Dictionary<string, RedisLibrarianContainer>(names.Length, StringComparer.Ordinal);
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            if (names.Length == 0) return true;
            foreach (string name in names)
            {
                if (!_containers.TryGetValue(name, out var container))
                    _containers.Add(name, container = new RedisLibrarianContainer(name, this));
                containers.Add(name, (RedisLibrarianContainer)container);
            }
            lock (_batchLifetime) _activeBatches++;
        }
        try
        {
            IDatabase store = await GetStore(cancellationToken).NoSync();
            if (batch.Writes.Count == 0)
            {
                var keys = new RedisKey[batch.Conditions.Count];
                var values = new RedisValue[keys.Length * 2];
                for (var i = 0; i < keys.Length; i++)
                {
                    LibrarianCondition condition = batch.Conditions[i];
                    keys[i] = containers[condition.Container].BatchDocument(condition.Id);
                    values[i * 2] = condition.ExpectedValue is null ? "0" : "1";
                    values[i * 2 + 1] = condition.ExpectedValue ?? "";
                }
                cancellationToken.ThrowIfCancellationRequested();
                long result = (long)await store.ScriptEvaluateAsync(CheckConditionsScript, keys, values).WaitAsync(cancellationToken).NoSync();
                if (result < 0) RecordConditionConflict(batch.Conditions[checked((int)-result - 1)]);
                return result == 1;
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
                const int pipelineSize = 8;
                for (int offset = 0; offset < names.Length; offset += pipelineSize)
                {
                    int count = Math.Min(pipelineSize, names.Length - offset);
                    if (count == 1)
                    {
                        if (!await Prepare(offset).NoSync()) return false;
                        continue;
                    }
                    var preparations = new Task<bool>[count];
                    for (int i = 0; i < count; i++) preparations[i] = Prepare(offset + i).AsTask();
                    bool[] results = await Task.WhenAll(preparations).NoSync();
                    if (Array.IndexOf(results, false) >= 0) return false;
                }

                ValueTask<bool> Prepare(int i) =>
                    containers[names[i]].PrepareBatch(store, conditions[i], writes[i], commands, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                // Observe the commit outcome even if cancellation arrives after submission.
                long outcome = (long)await commands.Execute(store).NoSync();
                if (outcome == 1) return true;
                if (outcome == -1)
                {
                    BatchConflicts.Add(1, new KeyValuePair<string, object?>("condition", "document_at_commit"));
                    return false;
                }
                BatchConflicts.Add(1, new KeyValuePair<string, object?>("condition", "schema_or_version"));
                if (attempt >= 127) throw new TimeoutException("The Librarian batch repeatedly conflicted with concurrent writes.");
                await Task.Delay(Random.Shared.Next(1, 10), cancellationToken).NoSync();
            }
        }
        finally { EndBatch(); }
    }
}
