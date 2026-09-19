using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Librarian.Abstractions.Transactions;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer
{
    internal RedisKey BatchDocument(string id) => Document(Id(id));

    internal async ValueTask<Action<ITransaction, List<Task>>?> PrepareBatch(IDatabase store,
        IEnumerable<LibrarianCondition> conditions, IEnumerable<LibrarianWrite> writes, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        RedisValue version = await store.StringGetAsync(Version).NoSync();
        LibrarianCondition[] conditionArray = conditions.ToArray();
        if (conditionArray.Length > 0)
        {
            var keys = new RedisKey[conditionArray.Length];
            for (int i = 0; i < keys.Length; i++) keys[i] = BatchDocument(conditionArray[i].Id);
            var values = (RedisResult[])(await store.ScriptEvaluateAsync(ReadItemsScript, keys).NoSync())!;
            for (int i = 0; i < values.Length; i++)
                if (!string.Equals(values[i].IsNull ? null : (string?)values[i], conditionArray[i].ExpectedValue, StringComparison.Ordinal)) return null;
        }
        var actions = new List<Action<ITransaction, List<Task>>>();
        var deltas = new Dictionary<(string Path, string Value), long>();
        LibrarianWrite[] writeArray = writes.ToArray();
        RedisValue[] paths = writeArray.Length == 0 ? [] : await store.SetMembersAsync(Schema).NoSync();
        var fields = new RedisValue[paths.Length];
        for (int i = 0; i < paths.Length; i++) fields[i] = Field(paths[i].ToString());
        foreach (LibrarianWrite write in writeArray)
        {
            token.ThrowIfCancellationRequested();
            string id = Id(write.Id);
            using JsonDocument? json = write.Value is not null && paths.Length > 0 ? JsonDocument.Parse(write.Value) : null;
            RedisValue[] previousValues = paths.Length == 0 ? [] : await store.HashGetAsync(Document(id), fields).NoSync();
            for (int i = 0; i < paths.Length; i++)
            {
                var path = paths[i].ToString();
                RedisValue old = previousValues[i];
                string? next = json is null ? null : RedisIndexValue.Read(json.RootElement, path);
                if (string.Equals(old.IsNull ? null : old.ToString(), next, StringComparison.Ordinal)) continue;
                if (!old.IsNull)
                {
                    var previous = old.ToString();
                    deltas[(path, previous)] = deltas.GetValueOrDefault((path, previous)) - 1;
                    actions.Add((transaction, commands) =>
                    {
                        commands.Add(transaction.SortedSetRemoveAsync(Index(path), previous + "!" + id));
                        commands.Add(transaction.SetRemoveAsync(Bucket(path, previous), id));
                    });
                }
                if (next is null)
                {
                    actions.Add((transaction, commands) =>
                    {
                        commands.Add(transaction.HashDeleteAsync(Document(id), Field(path)));
                        commands.Add(transaction.HashDeleteAsync(Document(id), SortField(path)));
                        commands.Add(transaction.SetRemoveAsync(Present(path), id));
                    });
                }
                else
                {
                    deltas[(path, next)] = deltas.GetValueOrDefault((path, next)) + 1;
                    actions.Add((transaction, commands) =>
                    {
                        commands.Add(transaction.HashSetAsync(Document(id), Field(path), next));
                        commands.Add(transaction.HashSetAsync(Document(id), SortField(path), next + "!" + id));
                        commands.Add(transaction.SortedSetAddAsync(Index(path), next + "!" + id, 0));
                        commands.Add(transaction.SetAddAsync(Bucket(path, next), id));
                        commands.Add(transaction.SetAddAsync(Present(path), id));
                    });
                }
            }
            actions.Add((transaction, commands) =>
            {
                if (write.Value is null)
                {
                    commands.Add(transaction.KeyDeleteAsync(Document(id)));
                    commands.Add(transaction.SetRemoveAsync(Ids, id));
                }
                else
                {
                    commands.Add(transaction.HashSetAsync(Document(id), "json", write.Value));
                    commands.Add(transaction.HashSetAsync(Document(id), "id", write.Id, When.NotExists));
                    commands.Add(transaction.SetAddAsync(Ids, id));
                }
            });
        }
        foreach (KeyValuePair<(string Path, string Value), long> pair in deltas)
        {
            if (pair.Value == 0) continue;
            long count = await store.SetLengthAsync(Bucket(pair.Key.Path, pair.Key.Value)).NoSync() + pair.Value;
            actions.Add((transaction, commands) => commands.Add(count <= 0
                ? transaction.SortedSetRemoveAsync(Distinct(pair.Key.Path), pair.Key.Value)
                : transaction.SortedSetAddAsync(Distinct(pair.Key.Path), pair.Key.Value, 0)));
        }
        return (transaction, commands) =>
        {
            transaction.AddCondition(version.IsNull ? Condition.KeyNotExists(Version) : Condition.StringEqual(Version, version));
            foreach (Action<ITransaction, List<Task>> action in actions) action(transaction, commands);
            if (writeArray.Length > 0) commands.Add(transaction.StringIncrementAsync(Version));
        };
    }
}
