using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;

namespace Soenneker.Librarian.Mongo;

internal static class MongoJsonSerializers
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, ConcurrentDictionary<Type, IBsonSerializer>> _serializers = new();

    internal static IBsonSerializer Create(Type type, JsonSerializerOptions options) =>
        _serializers.GetOrCreateValue(options).GetOrAdd(type, type => Build(type, options));

    private static IBsonSerializer Build(Type type, JsonSerializerOptions options)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return New(typeof(NullableSerializer<>).MakeGenericType(underlying), Create(underlying, options));
        JsonTypeInfo info = options.GetTypeInfo(type);
        if (info.Kind == JsonTypeInfoKind.Enumerable)
        {
            Type item = type.IsArray ? type.GetElementType()! : Interfaces(type)
                .First(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)).GetGenericArguments()[0];
            Type serializer = type.IsArray ? typeof(ArraySerializer<>).MakeGenericType(item)
                : typeof(EnumerableInterfaceImplementerSerializer<,>).MakeGenericType(type, item);
            return New(serializer, Create(item, options));
        }
        if (info.Kind == JsonTypeInfoKind.Dictionary)
        {
            Type dictionary = Interfaces(type).First(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            Type[] arguments = dictionary.GetGenericArguments();
            return New(typeof(DictionaryInterfaceImplementerSerializer<,,>).MakeGenericType(type, arguments[0], arguments[1]),
                DictionaryRepresentation.Document, Create(arguments[0], options), Create(arguments[1], options));
        }
        return New((info.Kind == JsonTypeInfoKind.Object ? typeof(MongoJsonDocumentSerializer<>) : typeof(MongoJsonSerializer<>)).MakeGenericType(type), info);
    }

    private static IEnumerable<Type> Interfaces(Type type) => type.GetInterfaces().Prepend(type);
    private static IBsonSerializer New(Type type, params object[] arguments) => (IBsonSerializer)Activator.CreateInstance(type, arguments)!;
}
