using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.PooledStringBuilders;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianDatabase
{
    /// <summary>Reads UTC time from the primary owning this database's Redis Cluster hash slot, without scripts.</summary>
    public async ValueTask<DateTimeOffset> GetServerTime(CancellationToken cancellationToken = default)
    {
        IDatabase store = await GetStore(cancellationToken).NoSync();
        string tag;
        var identity = new PooledStringBuilder(checked(_key.Length * 4));
        try
        {
            RedisIndexValue.AppendHex(ref identity, _key);
            tag = _sha256HashingUtil.Hash(identity.AsSpan()).ToUpperInvariant();
        }
        finally { identity.Dispose(); }
        var endpoint = await store.IdentifyEndpointAsync("librarian:{" + tag + "}:batches:clock", CommandFlags.DemandMaster).WaitAsync(cancellationToken).NoSync();
        if (endpoint is null) throw new InvalidOperationException("Redis primary could not be resolved.");
        DateTime time = await store.Multiplexer.GetServer(endpoint).TimeAsync(CommandFlags.DemandMaster).WaitAsync(cancellationToken).NoSync();
        return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
    }
}
