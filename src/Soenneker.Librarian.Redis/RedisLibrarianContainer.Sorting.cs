using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer
{
    // Older index definitions have scalar fields but no deterministic value+ID sort fields.
    private async ValueTask EnsureSortFields(IDatabase store, string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (await store.SetContainsAsync(SortSchema, path).NoSync()) return;
            RedisValue version = await store.StringGetAsync(Version).NoSync();
            RedisValue[] entries = await store.SortAsync(Present(path), sortType: SortType.Alphabetic, get: ["#", DocumentPattern(Field(path))]).NoSync();
            token.ThrowIfCancellationRequested();
            ITransaction transaction = Transaction(store, version);
            var commands = new List<Task>();
            for (var i = 0; i < entries.Length; i += 2)
                if (!entries[i + 1].IsNull)
                    commands.Add(transaction.HashSetAsync(Document(entries[i].ToString()), SortField(path), entries[i + 1] + "!" + entries[i]));
            commands.Add(transaction.SetAddAsync(SortSchema, path));
            commands.Add(transaction.StringIncrementAsync(Version));
            if (await Commit(transaction, commands).NoSync()) return;
        }
    }
}
