using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Stores generated accessors without discovering CLR members at runtime.</summary>
public static class GeneratedQueryMetadata
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), GeneratedQueryMember> Members = new();

    /// <summary>Registers a generated accessor. Call during application initialization.</summary>
    public static void Register<T, TValue>(string member, string jsonName, Func<T, TValue> getter, bool canIndex,
        Func<T, GeneratedIndexKey>? indexKey = null)
    {
        ArgumentNullException.ThrowIfNull(getter);
        Members.TryAdd((typeof(T), member), new GeneratedQueryMember(jsonName, value => getter((T)value), canIndex,
            indexKey is null ? null : value => indexKey((T)value)));
    }

    /// <summary>Finds metadata for a member declared on the specified type.</summary>
    public static bool TryGet(Type declaringType, string member, out GeneratedQueryMember metadata) =>
        Members.TryGetValue((declaringType, member), out metadata!);

    /// <summary>Returns generated CLR-to-JSON member mappings for explicit Mongo serializer registration.</summary>
    public static IReadOnlyDictionary<string, string> MemberNames<T>()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Members)
            if (pair.Key.Type == typeof(T)) result.Add(pair.Key.Name, pair.Value.JsonName);
        return result;
    }
}
