using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer
{
    public async ValueTask<List<T>> QueryItems<T>(QueryDefinition query, double? delayMs = null,
        CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(query);
        TimeSpan? delay = delayMs.HasValue ? TimeSpan.FromMilliseconds(delayMs.Value) : null;
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delayMs));
        using FeedIterator<T> iterator = store.GetItemQueryIterator<T>(query, requestOptions: QueryOptions);
        var result = new List<T>();
        while (iterator.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FeedResponse<T> page = await iterator.ReadNextAsync(cancellationToken).NoSync();
            database.Observe(name, "SqlQuery", page.StatusCode, page.Headers, page.Diagnostics);
            if (page.Count == 0) continue;
            result.EnsureCapacity(result.Count + page.Count);
            result.AddRange(page.Resource);
            if (delay.HasValue) await Task.Delay(delay.Value, cancellationToken).NoSync();
        }
        return result;
    }

    public async ValueTask QueryPages<T>(QueryDefinition query, int pageSize, Func<List<T>, ValueTask> consume,
        CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(consume);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        QueryRequestOptions options = QueryOptions;
        options.MaxItemCount = pageSize;
        using FeedIterator<T> iterator = store.GetItemQueryIterator<T>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FeedResponse<T> page = await iterator.ReadNextAsync(cancellationToken).NoSync();
            database.Observe(name, "SqlQueryPage", page.StatusCode, page.Headers, page.Diagnostics);
            await consume(CosmosFeedPage.Materialize(page)).NoSync();
        }
    }

    public async ValueTask<LibrarianPage<T>> QueryPage<T>(QueryDefinition query, int pageSize = 100,
        string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        QueryRequestOptions options = QueryOptions;
        options.MaxItemCount = pageSize;
        using FeedIterator<T> iterator = store.GetItemQueryIterator<T>(query, continuationToken, options);
        FeedResponse<T> response = await iterator.ReadNextAsync(cancellationToken).NoSync();
        database.Observe(name, "SqlQueryPage", response.StatusCode, response.Headers, response.Diagnostics);
        return new LibrarianPage<T>(CosmosFeedPage.Materialize(response), string.IsNullOrEmpty(response.ContinuationToken) ? null : response.ContinuationToken);
    }
}
