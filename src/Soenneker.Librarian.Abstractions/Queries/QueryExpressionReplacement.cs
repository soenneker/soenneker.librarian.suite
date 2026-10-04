using System.Linq.Expressions;

namespace Soenneker.Librarian.Abstractions.Queries;

internal sealed class QueryExpressionReplacement(Expression source, Expression replacement) : ExpressionVisitor
{
    public override Expression? Visit(Expression? node) => node == source ? replacement : base.Visit(node);
}
