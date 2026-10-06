using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Soenneker.Librarian.Generators;

// Follow symbols, not method spellings. Ambiguous helpers, fields, and mixed-provider locals
// remain opt-in. Inspect every write (including loop back edges) before classifying a local.
internal static class QueryDiscovery
{
    internal static bool IsQueryRoot(IMethodSymbol method, Compilation compilation)
    {
        if (method.Name is not ("BuildQueryable" or "BuildQueryableAcrossPartitions") || method.TypeArguments.Length != 1)
            return false;
        foreach (string name in new[]
        {
            "Soenneker.Librarian.Abstractions.ILibrarianContainer",
            "Soenneker.Librarian.Abstractions.ILibrarianDatabase",
            "Soenneker.Librarian.Abstractions.ILibrarianRepository`1"
        })
        {
            var contract = compilation.GetTypeByMetadataName(name);
            if (contract is null) continue;
            if (SymbolEqualityComparer.Default.Equals(method.ContainingType.OriginalDefinition, contract)) return true;
            foreach (var implemented in method.ContainingType.AllInterfaces)
            {
                if (!SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, contract)) continue;
                foreach (var member in implemented.GetMembers(method.Name))
                    if (method.ContainingType.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation &&
                        SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method.OriginalDefinition)) return true;
            }
        }
        return false;
    }

    internal static INamedTypeSymbol? DocumentType(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetSymbolInfo(context.Node).Symbol is not IMethodSymbol method || method.TypeArguments.Length != 1)
            return null;
        bool registration = method.Name == "Register" && SymbolEqualityComparer.Default.Equals(method.ContainingType,
            context.SemanticModel.Compilation.GetTypeByMetadataName("Soenneker.Librarian.Abstractions.Serialization.LibrarianJson"));
        if (!registration && !IsQueryRoot(method, context.SemanticModel.Compilation)) return null;
        return method.TypeArguments[0] is INamedTypeSymbol { SpecialType: SpecialType.None, TypeKind: TypeKind.Class or TypeKind.Struct, IsAnonymousType: false } type &&
            type.ContainingNamespace.ToDisplayString() != "System" &&
            !type.ContainingNamespace.ToDisplayString().StartsWith("System.", System.StringComparison.Ordinal)
            ? type : null;
    }

    internal static ExpressionSyntax? Source(InvocationExpressionSyntax syntax, IMethodSymbol method)
    {
        if (method.ReducedFrom is not null && syntax.Expression is MemberAccessExpressionSyntax member) return member.Expression;
        // Named arguments need not be in declaration order.
        string name = method.Parameters[0].Name;
        return syntax.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name)?.Expression
            ?? syntax.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon is null)?.Expression;
    }

    internal static bool IsLibrarian(ExpressionSyntax? expression, SemanticModel model)
    {
        if (expression is null) return false;
        return Follow(expression, model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0, out bool root) && root;
    }

    private static bool Follow(ExpressionSyntax expression, SemanticModel model, HashSet<ISymbol> visiting, int depth, out bool root)
    {
        root = false;
        if (depth > 64) return false;
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parentheses:
                return Follow(parentheses.Expression, model, visiting, depth + 1, out root);
            case CastExpressionSyntax cast:
                return Follow(cast.Expression, model, visiting, depth + 1, out root);
            case AwaitExpressionSyntax awaited:
                return Follow(awaited.Expression, model, visiting, depth + 1, out root);
            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return Follow(postfix.Operand, model, visiting, depth + 1, out root);
            case ConditionalExpressionSyntax conditional:
                bool first = Follow(conditional.WhenTrue, model, visiting, depth + 1, out bool firstRoot);
                bool second = Follow(conditional.WhenFalse, model, visiting, depth + 1, out bool secondRoot);
                root = firstRoot || secondRoot;
                return first && second;
            case InvocationExpressionSyntax invocation:
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
                if (IsQueryRoot(method, model.Compilation)) { root = true; return true; }
                if (method.ContainingType.ToDisplayString() == "System.Linq.Queryable" && Source(invocation, method) is { } source)
                    return Follow(source, model, visiting, depth + 1, out root);
                if (method.Name == "ConfigureAwait" && method.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
                    invocation.Expression is MemberAccessExpressionSyntax configure)
                    return Follow(configure.Expression, model, visiting, depth + 1, out root);
                return false;
        }

        var queryType = model.Compilation.GetTypeByMetadataName("Soenneker.Librarian.Abstractions.Queries.LibrarianQueryable`1");
        if (model.GetTypeInfo(expression).Type is INamedTypeSymbol type && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, queryType))
        {
            root = true;
            return true;
        }
        if (model.GetSymbolInfo(expression).Symbol is not ILocalSymbol local) return false;
        if (!visiting.Add(local)) return true; // A cycle is only accepted if another edge supplies a real root.
        try
        {
            if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax declaration ||
                declaration.SyntaxTree != model.SyntaxTree) return false;
            SyntaxNode? scope = declaration.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or
                LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) ?? declaration.SyntaxTree.GetRoot();
            var values = new List<ExpressionSyntax>();
            if (declaration.Initializer is { } initializer) values.Add(initializer.Value);
            foreach (var assignment in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local)) continue;
                if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;
                values.Add(assignment.Right);
            }
            // Passing the local by reference or assigning through deconstruction invalidates provenance.
            foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, local)) continue;
                if (identifier.Parent is RefExpressionSyntax) return false;
                if (identifier.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 }) return false;
                if (identifier.Ancestors().OfType<AssignmentExpressionSyntax>().Any(a =>
                    a.Left is TupleExpressionSyntax && a.Left.Span.Contains(identifier.Span))) return false;
            }
            if (values.Count == 0) return false;
            foreach (var value in values)
            {
                if (!Follow(value, model, visiting, depth + 1, out bool valueRoot)) return false;
                root |= valueRoot;
            }
            return true;
        }
        finally { visiting.Remove(local); }
    }
}
