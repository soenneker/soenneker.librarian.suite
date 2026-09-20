using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Postgres;

internal sealed class PostgresQueryProvider<T>(PostgresLibrarianContainer container) : ILibrarianAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression)
    {
        return QueryTypes.CreateQuery(this, expression);
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!typeof(IQueryable<TElement>).IsAssignableFrom(expression.Type))
            throw new ArgumentException("Expression must represent a queryable sequence of the requested type.", nameof(expression));
        return new PostgresQueryable<TElement>(this, expression);
    }

    public object? Execute(Expression expression) => Execute<object?>(expression);

    public TResult Execute<TResult>(Expression expression) => ExecuteAsync<TResult>(expression).AsTask().GetAwaiter().GetResult();

    public async ValueTask<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = PostgresQueryPlan.Create(expression, this);
        object? result = await container.ExecuteQuery(plan, cancellationToken).NoSync();
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Terminal == nameof(Queryable.Count)) return (TResult)(object)checked((int)(long)result!);
        if (plan.Terminal == nameof(Queryable.LongCount)) return (TResult)result!;
        if (plan.Terminal == nameof(Queryable.Any)) return (TResult)(object)((long)result! != 0);
        if (plan.Terminal == nameof(Queryable.All)) return (TResult)(object)((long)result! == 0);
        if (plan.Aggregate)
        {
            Type returnType = expression.Type;
            Type type = Nullable.GetUnderlyingType(returnType) ?? returnType;
            if (result is null)
            {
                if (plan.Terminal == nameof(Queryable.Sum)) result = QueryTypes.Get(type).Default;
                else if (Nullable.GetUnderlyingType(returnType) is null) throw new InvalidOperationException("Sequence contains no elements");
                else return default!;
            }
            return (TResult)Convert.ChangeType(result!, type, CultureInfo.InvariantCulture);
        }

        Type elementType = plan.Projection?.ResultType ?? typeof(T);
        QueryType resultType = QueryTypes.Get(elementType);
        IList items = resultType.CreateList();
        if (plan.Projection is not null)
            foreach (string?[] row in (List<string?[]>)result!) { cancellationToken.ThrowIfCancellationRequested(); items.Add(plan.Projection.Materialize(row)); }
        else
            foreach (string document in (List<string>)result!) { cancellationToken.ThrowIfCancellationRequested(); items.Add(LibrarianJson.Deserialize<T>(document)!); }

        if (plan.Terminal is null) return (TResult)items;
        if (plan.Terminal is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault) && items.Count > 1)
            throw new InvalidOperationException("Sequence contains more than one element");
        if (items.Count > 0) return (TResult)items[0]!;
        if (plan.Terminal is nameof(Queryable.First) or nameof(Queryable.Single)) throw new InvalidOperationException("Sequence contains no elements");
        return (TResult)resultType.Default!;
    }
}
