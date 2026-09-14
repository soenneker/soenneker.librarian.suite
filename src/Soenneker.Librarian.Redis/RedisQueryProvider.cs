using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Soenneker.Utils.Json;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

internal sealed class RedisQueryProvider<T>(RedisLibrarianContainer container) : IQueryProvider
{
    public IQueryable CreateQuery(Expression expression) => new RedisQueryable<T>(this, expression);
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        if (typeof(TElement) != typeof(T)) throw new NotSupportedException("Redis queries do not support projection. Materialize the server page before projecting.");
        return (IQueryable<TElement>)(object)new RedisQueryable<T>(this, expression);
    }
    public object? Execute(Expression expression) => Execute<object?>(expression);
    public TResult Execute<TResult>(Expression expression)
    {
        var plan = RedisQueryPlan.Create(expression, this);
        RedisResult result = container.ExecuteQuery(plan).AsTask().GetAwaiter().GetResult();
        if (plan.Terminal == nameof(Queryable.Count)) return (TResult)(object)checked((int)(long)result);
        if (plan.Terminal == nameof(Queryable.LongCount)) return (TResult)(object)(long)result;
        if (plan.Terminal == nameof(Queryable.Any)) return (TResult)(object)((long)result != 0);
        RedisResult[] documents = (RedisResult[]?)result ?? [];
        var items = new List<T>(documents!.Length);
        foreach (RedisResult document in documents) items.Add(JsonUtil.Deserialize<T>(document.ToString())!);
        object? value = plan.Terminal switch
        {
            nameof(Queryable.First) => items.First(),
            nameof(Queryable.FirstOrDefault) => items.FirstOrDefault(),
            nameof(Queryable.Single) => items.Single(),
            nameof(Queryable.SingleOrDefault) => items.SingleOrDefault(),
            _ => items
        };
        return (TResult)value!;
    }
}
