using System;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Postgres;

// Rewriting SQL expressions needs a body and parameter, not a runtime-created delegate type.
internal sealed record PostgresLambda(Expression Body, ParameterExpression[] Parameters)
{
    internal Type ReturnType => Body.Type;
}
