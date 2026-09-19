using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Utils.Json;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

internal sealed class RedisQueryProvider<T>(RedisLibrarianContainer container) : ILibrarianAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Type? queryType = expression.Type.GetInterfaces().Append(expression.Type)
            .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>));
        if (queryType is null) throw new ArgumentException("Expression must represent a queryable sequence.", nameof(expression));
        return (IQueryable)Activator.CreateInstance(typeof(RedisQueryable<>).MakeGenericType(queryType.GetGenericArguments()[0]), this, expression)!;
    }
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!typeof(IQueryable<TElement>).IsAssignableFrom(expression.Type)) throw new ArgumentException("Invalid query element type.", nameof(expression));
        return new RedisQueryable<TElement>(this, expression);
    }
    public object? Execute(Expression expression) => Execute<object?>(expression);
    public TResult Execute<TResult>(Expression expression) => ExecuteAsync<TResult>(expression).AsTask().GetAwaiter().GetResult();

    public async ValueTask<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = RedisQueryPlan.Create(expression, this);
        RedisResult result = await container.ExecuteQuery(plan, cancellationToken).NoSync();
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Terminal == nameof(Queryable.Count)) return (TResult)(object)checked((int)(long)result);
        if (plan.Terminal == nameof(Queryable.LongCount)) return (TResult)(object)(long)result;
        if (plan.Terminal == nameof(Queryable.Any)) return (TResult)(object)((long)result != 0);
        if (plan.Terminal == nameof(Queryable.All)) return (TResult)(object)((long)result == 0);
        RedisResult[] documents = (RedisResult[]?)result ?? [];
        Type elementType = plan.Projection?.ResultType ?? typeof(T);
        var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (RedisResult document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(plan.Projection is null ? JsonUtil.Deserialize<T>(document.ToString()) : plan.Projection.FromDocument(document.ToString()));
        }
        if (plan.Terminal is null) return (TResult)items;
        if (plan.Terminal is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault) && items.Count > 1)
            throw new InvalidOperationException("Sequence contains more than one element");
        if (items.Count > 0) return (TResult)items[0]!;
        if (plan.Terminal is nameof(Queryable.First) or nameof(Queryable.Single)) throw new InvalidOperationException("Sequence contains no elements");
        return (TResult)(elementType.IsValueType ? Activator.CreateInstance(elementType) : null)!;
    }
}
