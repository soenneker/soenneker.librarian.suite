using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MongoDB.Bson.Serialization;

namespace Soenneker.Librarian.Mongo;

/// <summary>Explicit BSON serializer factories for typed Mongo queries. Register documents, scalars, and nested collections before use.</summary>
public sealed class MongoJsonSerializerRegistry
{
    private readonly Dictionary<Type, Func<JsonSerializerOptions, IBsonSerializer>> _factories = new();

    /// <summary>Registers a scalar serializer using generated JSON metadata.</summary>
    public void RegisterScalar<T>() => Register<T>(options => new MongoJsonSerializer<T>(options.GetTypeInfo(typeof(T))));

    /// <summary>Registers a document and its CLR member names mapped to explicit JSON property names.
    /// A null mapping uses the serialization options' naming policy.</summary>
    public void RegisterDocument<T>(IReadOnlyDictionary<string, string?> members)
    {
        var mappings = new Dictionary<string, string?>(members, StringComparer.Ordinal);
        Register<T>(options =>
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in mappings)
                names.Add(pair.Key, pair.Value ?? options.PropertyNamingPolicy?.ConvertName(pair.Key) ?? pair.Key);
            return new MongoJsonDocumentSerializer<T>((JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)), names, Create);
        });
    }

    /// <summary>Registers a statically constructed BSON serializer, including collection and nullable serializers.</summary>
    public void Register<T>(Func<JsonSerializerOptions, IBsonSerializer<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories.Add(typeof(T), options => factory(options));
    }

    /// <summary>Resolves an explicitly registered serializer with the document's JSON options.</summary>
    public IBsonSerializer Create(Type type, JsonSerializerOptions options) => _factories.TryGetValue(type, out var factory)
        ? factory(options) : throw new InvalidOperationException($"Register a BSON serializer for {type} before building a Mongo query.");
}
