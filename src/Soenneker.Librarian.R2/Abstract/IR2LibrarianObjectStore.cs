using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.R2.Abstract;

/// <summary>Strongly consistent R2 object operations. Conditions must be evaluated atomically by R2.</summary>
public interface IR2LibrarianObjectStore
{
    /// <summary>Reads bytes and their ETag together; returns null only for a missing object. Enforces the supplied byte limit.</summary>
    ValueTask<R2LibrarianObject?> Read(string key, int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default);

    /// <summary>Writes bytes if the ETag matches, or creates only when absent if expectedETag is null. Returns the new ETag, or null on a confirmed conflict.</summary>
    /// <remarks>Never retry transport failures automatically: a dispatched write can have committed despite a lost response.</remarks>
    ValueTask<string?> Write(string key, ReadOnlyMemory<byte> content, string? expectedETag = null, CancellationToken cancellationToken = default);

    /// <summary>Lists one page of object keys under a prefix. Listings are not a multi-object transactional snapshot.</summary>
    ValueTask<R2LibrarianObjectPage> List(string prefix, string? cursor = null, CancellationToken cancellationToken = default);
}
