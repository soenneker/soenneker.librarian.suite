using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class LibrarianQueryProvider<T>(LibrarianContainer container) : ILibrarianAsyncQueryProvider, ILibrarianSequenceProvider
{
    public IEnumerable<TElement> ExecuteSequence<TElement>(Expression expression)
    {
        if (typeof(TElement) == typeof(T))
        {
            var plan = QueryPlan.Create(expression, this);
            if (plan?.Prefix == expression)
                return plan.ResidualPredicate is not null
                    ? (IEnumerable<TElement>)container.QuerySource<T>(plan).GetAwaiter().GetResult()
                    : (IEnumerable<TElement>)container.QuerySnapshot<T>(plan).GetAwaiter().GetResult();
            if (expression is ConstantExpression { Value: IQueryable query } && query.Provider == this)
                return (IEnumerable<TElement>)container.QuerySource<T>(null).GetAwaiter().GetResult();
        }
        if (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            string method = call.Method.Name;
            if (method is nameof(Queryable.Take) or nameof(Queryable.Skip) && call.Arguments[1] is ConstantExpression { Value: int amount })
            {
                // Take(0) need not build an index or capture a scan snapshot.
                if (method == nameof(Queryable.Take) && amount <= 0)
                {
                    container.CheckQueryLifetime();
                    return Array.Empty<TElement>();
                }
                if (TryProjectedPage<TElement>(call, out IEnumerable<TElement>? projectedPage)) return projectedPage!;
                // A trailing Take can bound document loading before Select without changing selector evaluation.
                if (method == nameof(Queryable.Take)
                    && call.Arguments[0] is MethodCallExpression projection && projection.Method.DeclaringType == typeof(Queryable)
                    && projection.Method.Name == nameof(Queryable.Select)
                    && projection.Arguments[1] is UnaryExpression { Operand: Expression<Func<T, TElement>> project })
                {
                    var page = QueryPlan.Create(projection.Arguments[0], this);
                    if (page?.Prefix == projection.Arguments[0] && page.ResidualPredicate is null)
                    {
                        page.Take = Math.Min(page.Take, amount);
                        return container.QuerySnapshot<T>(page).GetAwaiter().GetResult().Select(QueryFunction<T, TElement>.Get(project));
                    }
                }
                IEnumerable<TElement> source = ExecuteSequence<TElement>(call.Arguments[0]);
                return method == nameof(Queryable.Take) ? source.Take(amount) : source.Skip(amount);
            }
            if (method == nameof(Queryable.Where) && call.Arguments[1] is UnaryExpression { Operand: Expression<Func<TElement, bool>> predicate })
                return SequenceSource<TElement>(call.Arguments[0]).Where(QueryFunction<TElement, bool>.Get(predicate));
            if (method == nameof(Queryable.Select) && call.Arguments[1] is UnaryExpression { Operand: Expression<Func<T, TElement>> selector })
                return SequenceSource<T>(call.Arguments[0]).Select(QueryFunction<T, TElement>.Get(selector));
        }
        return Execute<IEnumerable<TElement>>(expression);
    }

    private IEnumerable<TElement> SequenceSource<TElement>(Expression expression)
    {
        if (typeof(TElement) == typeof(T))
        {
            var plan = QueryPlan.Create(expression, this);
            if (plan?.Prefix == expression) return (IEnumerable<TElement>)container.QuerySource<T>(plan).GetAwaiter().GetResult();
        }
        return ExecuteSequence<TElement>(expression);
    }

    private bool TryProjectedPage<TElement>(MethodCallExpression expression, out IEnumerable<TElement>? result)
    {
        result = null;
        Expression source = expression;
        var count = 0;
        while (source is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable)
               && call.Method.Name is nameof(Queryable.Skip) or nameof(Queryable.Take)
               && call.Arguments[1] is ConstantExpression { Value: int })
        {
            count++;
            source = call.Arguments[0];
        }
        if (source is not MethodCallExpression projection || projection.Method.DeclaringType != typeof(Queryable)
            || projection.Method.Name != nameof(Queryable.Select)
            || projection.Arguments[1] is not UnaryExpression { Operand: Expression<Func<T, TElement>> selector }
            || QueryPlan.GetProperty(selector.Body, selector.Parameters[0]) is null) return false;
        var plan = QueryPlan.Create(projection.Arguments[0], this);
        if (plan is null || plan.ResidualPredicate is not null || plan.Prefix != projection.Arguments[0]) return false;
        // Only side-effect-free auto-property projections may move across Skip.
        InlineBuffer<MethodCallExpression> buffer = default;
        Span<MethodCallExpression> paging = count <= 8 ? buffer : new MethodCallExpression[count];
        source = expression;
        for (int i = count - 1; i >= 0; i--)
        {
            paging[i] = (MethodCallExpression)source;
            source = paging[i].Arguments[0];
        }
        for (var i = 0; i < count; i++)
        {
            MethodCallExpression page = paging[i];
            plan.ApplyPage(page.Method.Name, (int)((ConstantExpression)page.Arguments[1]).Value!);
        }
        result = container.QuerySnapshot<T>(plan).GetAwaiter().GetResult().Select(QueryFunction<T, TElement>.Get(selector));
        return true;
    }

    public IQueryable CreateQuery(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Type? queryType = expression.Type.GetInterfaces().Append(expression.Type)
            .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>));
        if (queryType is null) throw new ArgumentException("Expression must represent a queryable sequence.", nameof(expression));
        Type element = queryType.GetGenericArguments()[0];
        return (IQueryable)Activator.CreateInstance(typeof(LibrarianQueryable<>).MakeGenericType(element), this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!typeof(IQueryable<TElement>).IsAssignableFrom(expression.Type))
            throw new ArgumentException("Expression must represent a queryable sequence of the requested type.", nameof(expression));
        return new LibrarianQueryable<TElement>(this, expression);
    }
    public object? Execute(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (TryElement(expression, out object? element)) return element;
        expression = NormalizeTerminal(NormalizeProjectedCount(expression));
        if (TryCount(expression, out object? count)) return count;
        (IQueryable<T> source, Expression rewritten) = Prepare(expression);
        return source.Provider.Execute(rewritten);
    }

    public TResult Execute<TResult>(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (TryElement(expression, out object? element)) return (TResult)element!;
        expression = NormalizeTerminal(NormalizeProjectedCount(expression));
        if (TryCount(expression, out object? count)) return (TResult)count!;
        if (typeof(TResult) == typeof(IEnumerable<T>))
        {
            var plan = QueryPlan.Create(expression, this);
            if (plan?.Prefix == expression)
                return plan.ResidualPredicate is not null
                    ? (TResult)container.QuerySource<T>(plan).GetAwaiter().GetResult()
                    : (TResult)container.QuerySnapshot<T>(plan).GetAwaiter().GetResult();
        }
        (IQueryable<T> source, Expression rewritten) = Prepare(expression);
        if (rewritten == source.Expression && source is TResult direct) return direct;
        return source.Provider.Execute<TResult>(rewritten);
    }

    public async ValueTask<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        expression = NormalizeProjectedPaging(NormalizeTerminal(NormalizeProjectedCount(expression)));
        if (CountPlan(expression) is { } countPlan)
        {
            await container.QuerySnapshot<T>(countPlan, cancellationToken).ConfigureAwait(false);
            string method = ((MethodCallExpression)expression).Method.Name;
            object count = method switch { nameof(Queryable.Any) => countPlan.Count != 0, nameof(Queryable.LongCount) => (long)countPlan.Count, _ => countPlan.Count };
            return (TResult)count;
        }
        var plan = QueryPlan.Create(expression, this);
        IQueryable<T> source = (await container.QuerySource<T>(plan, cancellationToken).ConfigureAwait(false)).AsQueryable();
        Expression rewritten = new ReplaceSource(this, plan?.Prefix, source.Expression).Visit(expression)!;
        cancellationToken.ThrowIfCancellationRequested();
        if (rewritten == source.Expression && source is TResult direct) return direct;
        return source.Provider.Execute<TResult>(rewritten);
    }

    private static Expression NormalizeProjectedPaging(Expression expression)
    {
        var pages = new List<MethodCallExpression>();
        Expression source = expression;
        while (source is MethodCallExpression page && page.Method.DeclaringType == typeof(Queryable) &&
            page.Method.Name is nameof(Queryable.Skip) or nameof(Queryable.Take) && page.Arguments[1] is ConstantExpression { Value: int })
        {
            pages.Add(page);
            source = page.Arguments[0];
        }
        if (pages.Count == 0 || source is not MethodCallExpression projection || projection.Method.DeclaringType != typeof(Queryable) ||
            projection.Method.Name != nameof(Queryable.Select) || projection.Method.GetGenericArguments()[0] != typeof(T) ||
            projection.Arguments[1] is not UnaryExpression { Operand: LambdaExpression selector } || selector.Parameters.Count != 1 ||
            QueryPlan.GetProperty(selector.Body, selector.Parameters[0]) is null) return expression;
        source = projection.Arguments[0];
        for (int i = pages.Count - 1; i >= 0; i--)
            source = Expression.Call(typeof(Queryable), pages[i].Method.Name, [typeof(T)], source, pages[i].Arguments[1]);
        return Expression.Call(typeof(Queryable), nameof(Queryable.Select), [typeof(T), selector.ReturnType], source, projection.Arguments[1]);
    }

    // Count and Any do not need a selected scalar. Push their paging to the indexed source,
    // while leaving arbitrary projections and predicate overloads on the ordinary path.
    private static Expression NormalizeProjectedCount(Expression expression)
    {
        if (expression is not MethodCallExpression terminal || terminal.Method.DeclaringType != typeof(Queryable) ||
            terminal.Method.Name is not (nameof(Queryable.Count) or nameof(Queryable.LongCount) or nameof(Queryable.Any)) || terminal.Arguments.Count != 1)
            return expression;
        var pages = new List<MethodCallExpression>();
        Expression source = terminal.Arguments[0];
        while (source is MethodCallExpression page && page.Method.DeclaringType == typeof(Queryable) &&
            page.Method.Name is nameof(Queryable.Skip) or nameof(Queryable.Take) && page.Arguments[1] is ConstantExpression { Value: int })
        {
            pages.Add(page);
            source = page.Arguments[0];
        }
        if (source is not MethodCallExpression projection || projection.Method.DeclaringType != typeof(Queryable) ||
            projection.Method.Name != nameof(Queryable.Select) || projection.Method.GetGenericArguments()[0] != typeof(T) ||
            projection.Arguments[1] is not UnaryExpression { Operand: LambdaExpression selector } ||
            selector.Parameters.Count != 1 || QueryPlan.GetProperty(selector.Body, selector.Parameters[0]) is null)
            return expression;
        source = projection.Arguments[0];
        for (int i = pages.Count - 1; i >= 0; i--)
            source = Expression.Call(typeof(Queryable), pages[i].Method.Name, [typeof(T)], source, pages[i].Arguments[1]);
        return Expression.Call(typeof(Queryable), terminal.Method.Name, [typeof(T)], source);
    }
    private static Expression NormalizeTerminal(Expression expression)
    {
        if (expression is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable)
            || call.Method.Name is not (nameof(Queryable.First) or nameof(Queryable.FirstOrDefault) or nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault))
            || call.Method.GetGenericArguments()[0] != typeof(T) || call.Arguments.Count > 2) return expression;
        Expression source = call.Arguments[0];
        if (call.Arguments.Count == 2)
        {
            if (call.Arguments[1] is not UnaryExpression { Operand: LambdaExpression }) return expression;
            source = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [typeof(T)], source, call.Arguments[1]);
        }
        int limit = call.Method.Name is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault) ? 2 : 1;
        source = Expression.Call(typeof(Queryable), nameof(Queryable.Take), [typeof(T)], source, Expression.Constant(limit));
        return Expression.Call(typeof(Queryable), call.Method.Name, [typeof(T)], source);
    }

    private bool TryElement(Expression expression, out object? result)
    {
        result = null;
        if (expression is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable)
            || call.Method.Name is not (nameof(Queryable.First) or nameof(Queryable.FirstOrDefault) or nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault))
            || call.Method.GetGenericArguments()[0] != typeof(T) || call.Arguments.Count > 2) return false;
        Expression source = call.Arguments[0];
        QueryPlan? plan;
        if (call.Arguments.Count == 2)
        {
            if (call.Arguments[1] is not UnaryExpression { Operand: LambdaExpression predicate }) return false;
            plan = QueryPlan.CreatePredicate(source, predicate, this);
        }
        else plan = QueryPlan.Create(source, this);
        if (plan is null || plan.ResidualPredicate is not null || plan.Prefix != source) return false;
        bool single = call.Method.Name is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault);
        plan.Take = Math.Min(plan.Take, single ? 2 : 1);
        IReadOnlyList<T> items = container.QuerySnapshot<T>(plan).GetAwaiter().GetResult();
        if (single && items.Count > 1) throw new InvalidOperationException("Sequence contains more than one element");
        if (items.Count != 0) result = items[0];
        else if (call.Method.Name is nameof(Queryable.First) or nameof(Queryable.Single))
            throw new InvalidOperationException("Sequence contains no elements");
        else result = default(T);
        return true;
    }

    private QueryPlan? CountPlan(Expression expression)
    {
        if (expression is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable)
            || call.Method.Name is not (nameof(Queryable.Count) or nameof(Queryable.Any) or nameof(Queryable.LongCount))
            || call.Method.GetGenericArguments()[0] != typeof(T)) return null;
        Expression source = call.Arguments[0];
        QueryPlan? plan = call.Arguments.Count == 2 && call.Arguments[1] is UnaryExpression { Operand: LambdaExpression predicate }
            ? QueryPlan.CreatePredicate(source, predicate, this) : QueryPlan.Create(source, this);
        if (plan is null || plan.ResidualPredicate is not null || plan.Prefix != source) return null;
        plan.CountOnly = true;
        if (call.Method.Name == nameof(Queryable.Any)) plan.Take = Math.Min(plan.Take, 1);
        return plan;
    }

    private bool TryCount(Expression expression, out object? result)
    {
        result = null;
        if (CountPlan(expression) is not { } plan) return false;
        container.QuerySnapshot<T>(plan).GetAwaiter().GetResult();
        result = ((MethodCallExpression)expression).Method.Name switch
        {
            nameof(Queryable.Any) => (object)(plan.Count != 0),
            nameof(Queryable.LongCount) => (long)plan.Count,
            _ => plan.Count
        };
        return true;
    }
    private (IQueryable<T>, Expression) Prepare(Expression expression)
    {
        var plan = QueryPlan.Create(expression, this);
        IQueryable<T> source = container.QuerySource<T>(plan).GetAwaiter().GetResult().AsQueryable();
        return (source, new ReplaceSource(this, plan?.Prefix, source.Expression).Visit(expression)!);
    }

}
