using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>A query provider supporting native continuation tokens.</summary>
public interface ILibrarianPagedQueryProvider : IQueryProvider
{
    /// <summary>Reads one server page with a positive item limit. Tokens must be reused with the same query and scope.</summary>
    /// <remarks>Pages are not a snapshot. Tokens are opaque, provider-specific, and may expire after backend or SDK changes.
    /// An empty page can have a continuation token. Unsupported query shapes throw rather than simulate offset paging.
    /// Currently supported by Cosmos. Tokens do not grant authorization; applications must enforce access to query scopes.</remarks>
    ValueTask<LibrarianPage<T>> ReadPage<T>(Expression expression, int pageSize, string? continuationToken = null,
        CancellationToken cancellationToken = default);
}
