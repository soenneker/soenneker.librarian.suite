using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Cosmos;

internal interface ICosmosPageReader
{
    /// <summary>Drains one translated query and iterator, awaiting each page consumer before fetching another page.</summary>
    ValueTask ReadPages<T>(Expression expression, int pageSize, string? continuationToken,
        Func<List<T>, ValueTask> consume, CancellationToken cancellationToken);
}
