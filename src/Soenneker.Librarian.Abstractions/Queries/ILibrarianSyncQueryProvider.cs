using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>A query provider with genuine synchronous execution, without blocking on asynchronous operations.</summary>
public interface ILibrarianSyncQueryProvider : IQueryProvider
{
    /// <summary>Executes an expression synchronously and returns its result.</summary>
    object? ExecuteSync(Expression expression);

    /// <summary>Executes an expression synchronously and returns its typed result.</summary>
    TResult ExecuteSync<TResult>(Expression expression);
}
