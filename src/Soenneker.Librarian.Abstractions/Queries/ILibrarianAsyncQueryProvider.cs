using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Executes Librarian query expressions without blocking on remote I/O.</summary>
public interface ILibrarianAsyncQueryProvider : IQueryProvider
{
    /// <summary>Executes a terminal or sequence expression with cancellation.</summary>
    /// <remarks>Remote providers await server operations. Local providers execute CPU work on the caller's thread.
    /// Cancellation does not undo an index construction or server operation already dispatched.</remarks>
    ValueTask<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default);
}
