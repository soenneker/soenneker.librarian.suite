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
    internal async ValueTask<Action<ITransaction, List<Task>>?> PrepareBatch(IDatabase store,
        IEnumerable<LibrarianCondition> conditions, IEnumerable<LibrarianWrite> writes, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        RedisValue version = await store.StringGetAsync(Version).NoSync();
        foreach (LibrarianCondition condition in conditions)
        {
            token.ThrowIfCancellationRequested();
            RedisValue value = await store.HashGetAsync(Document(Id(condition.Id)), "json").NoSync();
            if (!string.Equals(value.IsNull ? null : value.ToString(), condition.ExpectedValue, StringComparison.Ordinal)) return null;
        }
        var actions = new List<Action<ITransaction, List<Task>>>();
        var deltas = new Dictionary<(string Path, string Value), long>();
        LibrarianWrite[] writeArray = writes.ToArray();
        RedisValue[] paths = writeArray.Length == 0 ? [] : await store.SetMembersAsync(Schema).NoSync();
        foreach (LibrarianWrite write in writeArray)
        {
            token.ThrowIfCancellationRequested();
            string id = Id(write.Id);
            using JsonDocument? json = write.Value is not null && paths.Length > 0 ? JsonDocument.Parse(write.Value) : null;
            foreach (RedisValue pathValue in paths)
            {
                var path = pathValue.ToString();
                RedisValue old = await store.HashGetAsync(Document(id), Field(path)).NoSync();
                string? next = json is null ? null : RedisIndexValue.Read(json.RootElement, path);
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
