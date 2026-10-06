using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Soenneker.Librarian.Generators;

// Deliberately conservative: emitted lambdas must bind identically outside their original class.
internal sealed class QueryLambdaRewriter(SemanticModel model, LambdaExpressionSyntax lambda) : CSharpSyntaxRewriter
{
    internal string? Error { get; private set; }

    public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
    {
        Error = "the lambda captures this";
        return node;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        ISymbol? symbol = model.GetSymbolInfo(node).Symbol;
        switch (symbol)
        {
            case ILocalSymbol { IsConst: true } constant:
                // Keep numeric types and other constant conversions exactly as the original compiler bound them.
                Error = "a local constant requires explicit literal substitution";
                break;
            case ILocalSymbol:
                Error = "the lambda captures a local variable";
                break;
            case IParameterSymbol parameter
                when !parameter.DeclaringSyntaxReferences.Any(r => lambda.Span.Contains(r.Span)):
                Error = "the lambda captures an outer parameter";
                break;
            case INamedTypeSymbol type:
                if (!LibrarianGenerator.Accessible(type))
                    Error = "the lambda references an inaccessible type";
                if (node.Parent is not QualifiedNameSyntax && node.Parent is not AliasQualifiedNameSyntax)
                    return SyntaxFactory.ParseName(LibrarianGenerator.Name(type)).WithTriviaFrom(node);
                break;
            case IFieldSymbol or IPropertySymbol or IMethodSymbol:
                if (symbol.DeclaredAccessibility != Accessibility.Public)
                    Error = "the lambda references a non-public member";
                if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node)
                    break;
                if (node.Parent is NameEqualsSyntax ||
                    node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
                    break;
                if (symbol.IsStatic)
                    return SyntaxFactory
                           .ParseExpression(LibrarianGenerator.Name(symbol.ContainingType) + ".@" + symbol.Name)
                           .WithTriviaFrom(node);
                Error = "the lambda captures an instance member";
                break;
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Extension method binding depends on the original using directives; reject until explicitly lowered.
        if (model.GetSymbolInfo(node).Symbol is IMethodSymbol { ReducedFrom: not null })
            Error = "extension method calls in lambdas are not supported yet";
        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        Error = "generic syntax in lambdas is not supported yet";
        return node;
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol { DeclaredAccessibility: Accessibility.Public })
            Error = "the projection constructor is not public";
        return base.VisitObjectCreationExpression(node);
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        Error = "target-typed construction is not supported yet";
        return node;
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (model.GetSymbolInfo(node.Left).Symbol is IPropertySymbol property &&
            property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
            Error = "the projection setter is not public";
        return base.VisitAssignmentExpression(node);
    }

    public override SyntaxNode? VisitAnonymousObjectCreationExpression(AnonymousObjectCreationExpressionSyntax node)
    {
        Error = "anonymous projections are not supported yet";
        return node;
    }

    public override SyntaxNode? VisitQueryExpression(QueryExpressionSyntax node)
    {
        Error = "query comprehension syntax inside lambdas is not supported yet";
        return node;
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        if (model.GetSymbolInfo(node).Symbol is INamedTypeSymbol type)
        {
            if (!LibrarianGenerator.Accessible(type))
                Error = "the lambda references an inaccessible type";
            return SyntaxFactory.ParseName(LibrarianGenerator.Name(type)).WithTriviaFrom(node);
        }

        return base.VisitQualifiedName(node);
    }
}