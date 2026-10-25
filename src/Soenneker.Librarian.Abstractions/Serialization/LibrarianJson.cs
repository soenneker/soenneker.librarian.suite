using System;
using Soenneker.Utils.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Abstractions.Serialization;

/// <summary>Registers source-generated JSON contracts used by Librarian typed document operations.</summary>
/// <remarks>Register each document and enum type at startup, before using any containers. Contracts are process-wide,
/// immutable after registration, and must use the same JSON naming and converter rules as the stored documents.
/// There is no reflection-based fallback. Built-in scalar contracts use System.Text.Json web defaults.</remarks>
public static class LibrarianJson
{
    private static readonly ConcurrentDictionary<Type, JsonTypeInfo> _contracts = new();

    /// <summary>Registers a generated contract and statically roots the corresponding query result and collection types.</summary>
    public static void Register<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        typeInfo.MakeReadOnly();
        JsonTypeInfo registered = _contracts.GetOrAdd(typeof(T), typeInfo);
        if (!ReferenceEquals(registered, typeInfo))
            throw new InvalidOperationException($"A different JSON contract is already registered for {typeof(T)}.");
        QueryTypes.Register<T>();
    }

    public static JsonTypeInfo Contract(Type type) => _contracts.TryGetValue(type, out JsonTypeInfo? contract)
        ? contract : LibrarianScalarJsonContext.Default.GetTypeInfo(type)
        ?? throw new InvalidOperationException($"Register a source-generated JSON contract with LibrarianJson.Register<T>() before using {type}.");

    /// <summary>Resolves a query member against its registered serialization contract, including nested generated contracts.</summary>
    public static string PropertyName(Type type, string member, string? explicitName = null, Type? rootType = null)
    {
        JsonTypeInfo? contract = null;
        if (rootType is not null && _contracts.TryGetValue(rootType, out JsonTypeInfo? rootContract))
            rootContract.Options.TryGetTypeInfo(type, out contract);
        contract ??= _contracts.GetValueOrDefault(type);
        if (contract is null)
        {
            foreach (JsonTypeInfo root in _contracts.Values)
            {
                if (!root.Options.TryGetTypeInfo(type, out JsonTypeInfo? nested)) continue;
                if (contract is not null && Name(contract) != Name(nested))
                    throw new NotSupportedException($"Conflicting JSON member mappings for {type}.{member}.");
                contract = nested;
            }
        }
        if (contract is null) throw new NotSupportedException($"Register the JSON contract for {type} before querying its members.");
        string name = Name(contract);
        foreach (JsonPropertyInfo property in contract.Properties)
            if (property.Name == name && property.Get is not null && !property.IsExtensionData) return name;
        throw new NotSupportedException($"The JSON contract does not expose {type}.{member} as a queryable property.");

        string Name(JsonTypeInfo info) => explicitName ?? info.Options.PropertyNamingPolicy?.ConvertName(member) ?? member;
    }

    public static T? Deserialize<T>(string json) => JsonUtil.Deserialize(json, (JsonTypeInfo<T>)Contract(typeof(T)));
    // JsonUtil has no non-generic metadata overload; retain the registered AOT contract here.
    public static object? Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, Contract(type));
    public static string Serialize<T>(T value) => JsonUtil.Serialize(value, (JsonTypeInfo<T>)Contract(typeof(T)));
    // JsonUtil only accepts generic metadata; this path must preserve the runtime-selected registered contract.
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Contract(value?.GetType() ?? typeof(string)));
}
