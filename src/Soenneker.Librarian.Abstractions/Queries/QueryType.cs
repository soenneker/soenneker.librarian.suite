using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Abstractions.Queries;

public sealed record QueryType(Func<IList> CreateList, object? Default,
    Func<IQueryProvider, Expression, IQueryable> CreateQuery, Func<IEnumerable<object?>, object> CastSequence,
    Comparison<object?> Compare, Func<object?, object?, bool> Equal, Func<object?, object?> Cast);
