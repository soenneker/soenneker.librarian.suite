using System.Linq.Expressions;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class LocalExpressionReplacement(Expression source, Expression replacement) : ExpressionVisitor
{
    public override Expression? Visit(Expression? node) => node == source ? replacement : base.Visit(node);
}
