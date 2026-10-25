using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Turso;

internal sealed class TursoQueryProvider<T>(ILibrarianContainer container) : ILibrarianAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression) => QueryTypes.CreateQuery(this, expression);
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        if (typeof(TElement) != typeof(T)) throw new NotSupportedException("Remote projections are not supported by this provider.");
        return new LibrarianQueryable<TElement>(this, expression);
    }
    object? IQueryProvider.Execute(Expression expression) => throw new NotSupportedException("Synchronous query execution is not supported by this provider. Use Librarian asynchronous query operations.");
    TResult IQueryProvider.Execute<TResult>(Expression expression) => throw new NotSupportedException("Synchronous query execution is not supported by this provider. Use Librarian asynchronous query operations.");

    public async ValueTask<TResult> Execute<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var calls = new Stack<MethodCallExpression>();
        while (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        { calls.Push(call); expression = call.Arguments[0]; }
        if (expression is not ConstantExpression { Value: IQueryable root } || root.Provider != this)
            throw new NotSupportedException("The query must originate from this container.");
        string? path = null;
        object? value = null;
        bool equality = false, descending = false, paging = false;
        int skip = 0, take = int.MaxValue;
        string? terminal = null;
        foreach (MethodCallExpression call in calls)
        {
            switch (call.Method.Name)
            {
                case nameof(Queryable.Where) when !paging && !equality:
                    var predicate = (LambdaExpression)((UnaryExpression)call.Arguments[1]).Operand;
                    if (predicate.Body is not BinaryExpression { NodeType: ExpressionType.Equal, Right: ConstantExpression constant } comparison)
                        throw new NotSupportedException("This provider supports field == constant predicates. Use explicit index methods for captured values and ranges.");
                    string filterPath = Path(comparison.Left, predicate.Parameters[0]);
                    if (path is not null && path != filterPath) throw new NotSupportedException("Filtering and ordering must use the same index.");
                    path = filterPath; value = constant.Value; equality = true;
                    break;
                case nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) when !paging:
                    var selector = (LambdaExpression)((UnaryExpression)call.Arguments[1]).Operand;
                    string orderPath = Path(selector.Body, selector.Parameters[0]);
                    if (path is not null && path != orderPath) throw new NotSupportedException("Only one indexed ordering is supported.");
                    path = orderPath; descending = call.Method.Name == nameof(Queryable.OrderByDescending);
                    if (equality && descending) throw new NotSupportedException("Descending equality queries are not supported.");
                    break;
                case nameof(Queryable.Skip):
                    int amount = Math.Max(0, (int)((ConstantExpression)call.Arguments[1]).Value!);
                    amount = Math.Min(amount, take); skip = checked(skip + amount); take -= amount; paging = true;
                    break;
                case nameof(Queryable.Take):
                    take = Math.Min(take, Math.Max(0, (int)((ConstantExpression)call.Arguments[1]).Value!)); paging = true;
                    break;
                case nameof(Queryable.Count) or nameof(Queryable.LongCount) or nameof(Queryable.Any) when call.Arguments.Count == 1:
                    terminal = call.Method.Name; break;
                default: throw new NotSupportedException($"Queryable.{call.Method.Name} is not supported by this provider.");
            }
        }
        if (equality && descending) throw new NotSupportedException("Descending equality queries are not supported.");
        if (path is not null) await container.EnsureIndex(path, cancellationToken).ConfigureAwait(false);
        if (terminal is not null)
        {
            if (path is not null && !equality) throw new NotSupportedException("Counts over ordered ranges are not supported by this LINQ subset.");
            int count = equality ? await container.CountByIndex(path!, value, cancellationToken).ConfigureAwait(false)
                : await container.CountItems(cancellationToken).ConfigureAwait(false);
            count = Math.Min(take, Math.Max(0, count - skip));
            object result = terminal == nameof(Queryable.Any) ? (object)(count != 0) : terminal == nameof(Queryable.LongCount) ? (object)(long)count : count;
            return (TResult)result;
        }
        if (path is null || take == int.MaxValue)
            throw new NotSupportedException("Sequence queries require an indexed Where/OrderBy and a bounded Take. Use explicit container APIs for full enumeration.");
        if (take == 0) return (TResult)(object)new List<T>();
        LibrarianQueryResult<T> page = equality
            ? await container.FindByIndex<T>(path, value, skip, take, cancellationToken).ConfigureAwait(false)
            : await container.FindRangeByIndex<T>(path, descending: descending, skip: skip, take: take, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (TResult)(object)page.Items;
    }

    private static string Path(Expression expression, ParameterExpression parameter)
    {
        var names = new Stack<string>();
        while (expression is MemberExpression member)
        {
            if (!GeneratedQueryMetadata.TryGet(member.Member.DeclaringType!, member.Member.Name, out GeneratedQueryMember metadata))
                throw new NotSupportedException("LINQ field access requires Librarian generated query metadata. Explicit index methods accept JSON paths directly.");
            names.Push(Soenneker.Librarian.Abstractions.Serialization.LibrarianJson.PropertyName(
                member.Expression!.Type, member.Member.Name, metadata.JsonName, parameter.Type));
            expression = member.Expression!;
        }
        if (expression != parameter || names.Count == 0) throw new NotSupportedException("Expected a document field path.");
        return string.Join('.', names);
    }
}
