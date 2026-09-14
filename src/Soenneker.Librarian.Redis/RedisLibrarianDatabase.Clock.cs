using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianDatabase
{
    /// <summary>Reads UTC time from the primary owning this database's Redis Cluster hash slot, without scripts.</summary>
    public async ValueTask<DateTimeOffset> GetServerTime(CancellationToken cancellationToken = default)
    {
        IDatabase store = await GetStore(cancellationToken).NoSync();
        var endpoint = await store.IdentifyEndpointAsync(StoragePrefix + "clock", CommandFlags.DemandMaster).WaitAsync(cancellationToken).NoSync();
        if (endpoint is null) throw new InvalidOperationException("Redis primary could not be resolved.");
        DateTime time = await store.Multiplexer.GetServer(endpoint).TimeAsync(CommandFlags.DemandMaster).WaitAsync(cancellationToken).NoSync();
        return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
    }
}
