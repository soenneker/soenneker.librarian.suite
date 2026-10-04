using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Soenneker.Librarian.Postgres;

internal sealed class PostgresProjectionRewriter(ParameterExpression parameter, Expression selected) : ExpressionVisitor
{
    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression == parameter)
        {
            if (selected is NewExpression { Members: not null } constructor)
            {
                int index = constructor.Members.IndexOf(node.Member);
                if (index >= 0) return constructor.Arguments[index];
            }
            if (selected is MemberInitExpression initializer)
                foreach (MemberBinding binding in initializer.Bindings)
                    if (binding.Member == node.Member && binding is MemberAssignment assignment)
                    {
                        if (binding.Member is PropertyInfo property &&
                            (property.GetMethod is not { IsVirtual: false } getter || property.SetMethod is not { IsVirtual: false } setter ||
                             !getter.IsDefined(typeof(CompilerGeneratedAttribute), false) || !setter.IsDefined(typeof(CompilerGeneratedAttribute), false)))
                            throw PostgresQueryPlan.Unsupported();
                        return assignment.Expression;
                    }
            if (selected is NewExpression or MemberInitExpression) throw PostgresQueryPlan.Unsupported();
        }
        return base.VisitMember(node);
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        if (node != parameter) return node;
        if (selected is NewExpression or MemberInitExpression) throw PostgresQueryPlan.Unsupported();
        return selected;
    }
}
