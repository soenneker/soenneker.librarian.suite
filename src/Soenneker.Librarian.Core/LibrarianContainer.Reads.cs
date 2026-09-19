using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

public sealed partial class LibrarianContainer
{
    public async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        CancellationToken cancellationToken = default)
    {
        IndexKey? lower = minimum is null ? null : IndexKey.FromValue(minimum);
        IndexKey? upper = maximum is null ? null : IndexKey.FromValue(maximum);
        if (lower is { } min && upper is { } max && (min.Kind != max.Kind || min.CompareTo(max) > 0))
            throw new ArgumentException("Range bounds must have the same scalar type and minimum must not exceed maximum.");
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return GetIndex(fieldPath).CountRange(lower, upper);
        }
    }

    public async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var result = new string?[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[i] = _items.GetValueOrDefault(ids[i]);
            }
            return result;
        }
    }

    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return _items.Count;
        }
    }
}
