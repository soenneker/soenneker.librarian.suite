using System.Linq.Expressions;

namespace Soenneker.Librarian.Postgres;

internal sealed class PostgresParameterReference(ParameterExpression parameter) : ExpressionVisitor
{
    internal bool Found;
    protected override Expression VisitParameter(ParameterExpression node) { Found |= node == parameter; return node; }
}
