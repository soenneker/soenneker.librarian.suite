using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Constructs generated projections from provider-returned scalar columns.</summary>
public static class GeneratedQueryProjections
{
    private static readonly ConditionalWeakTable<Expression, Func<object?[], object?>> Factories = new();

    /// <summary>Registers a constructor for an original projection body in column order.</summary>
    public static void Register(Expression body, Func<object?[], object?> factory) => Factories.GetValue(body, _ => factory);

    /// <summary>Finds a generated materializer using the provider's validated column order.</summary>
    public static bool TryGet(Expression body, IReadOnlyList<(string Path, Type Type)> columns, out Func<string?[], object?> materialize)
    {
        if (!Factories.TryGetValue(body, out var factory))
        {
            materialize = null!;
            return false;
        }
        materialize = values =>
        {
            var arguments = new object?[columns.Count];
            for (int i = 0; i < arguments.Length; i++)
                arguments[i] = values[i] is { } json ? LibrarianJson.Deserialize(json, columns[i].Type) : QueryTypes.Get(columns[i].Type).Default;
            return factory(arguments);
        };
        return true;
    }
}
