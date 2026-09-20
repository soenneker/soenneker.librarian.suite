using System;
using System.Collections.Concurrent;
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

    internal static JsonTypeInfo Contract(Type type) => _contracts.TryGetValue(type, out JsonTypeInfo? contract)
        ? contract : LibrarianScalarJsonContext.Default.GetTypeInfo(type)
        ?? throw new InvalidOperationException($"Register a source-generated JSON contract with LibrarianJson.Register<T>() before using {type}.");

    internal static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)Contract(typeof(T)));
    internal static object? Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, Contract(type));
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, (JsonTypeInfo<T>)Contract(typeof(T)));
    internal static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Contract(value?.GetType() ?? typeof(string)));
}
