using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class ReplaceSource(IQueryProvider provider, Expression? prefix, Expression replacement) : ExpressionVisitor
{
    public override Expression? Visit(Expression? node)
    {
        if (node == prefix || prefix is null && node is ConstantExpression { Value: IQueryable query } && query.Provider == provider)
            return replacement;
        return base.Visit(node);
    }
}
