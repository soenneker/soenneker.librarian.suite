using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MongoDB.Bson.Serialization;

namespace Soenneker.Librarian.Mongo;

// Supply the stored JSON member names to LINQ3 without changing global BSON conventions.
internal sealed class MongoJsonDocumentSerializer<T> : MongoJsonSerializer<T>, IBsonDocumentSerializer
{
    private readonly Func<string, string> _memberName;
    private readonly Func<Type, JsonSerializerOptions, IBsonSerializer> _serializer;

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime BSON serializer discovery constructs generic types. Supply explicit serializer factories instead.")]
    public MongoJsonDocumentSerializer(JsonTypeInfo typeInfo) : base(typeInfo)
    {
        _memberName = DiscoverMemberName;
        _serializer = MongoJsonSerializers.Create;
    }

    internal MongoJsonDocumentSerializer(JsonTypeInfo<T> typeInfo, IReadOnlyDictionary<string, string> members,
        Func<Type, JsonSerializerOptions, IBsonSerializer> serializer) : base(typeInfo)
    {
        var names = new Dictionary<string, string>(members, StringComparer.Ordinal);
        _memberName = member => names.TryGetValue(member, out string? name) ? name : "";
        _serializer = serializer;
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    private string DiscoverMemberName(string memberName)
    {
        MemberInfo? member = typeof(T).GetMember(memberName, BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();
        return member?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? TypeInfo.Options.PropertyNamingPolicy?.ConvertName(memberName) ?? memberName;
    }

    private readonly ConcurrentDictionary<string, BsonSerializationInfo> _members = new(StringComparer.Ordinal);

    public bool TryGetMemberSerializationInfo(string memberName, out BsonSerializationInfo serializationInfo)
    {
        if (_members.TryGetValue(memberName, out serializationInfo!)) return true;
        string name = _memberName(memberName);
        JsonPropertyInfo? property = TypeInfo.Properties.FirstOrDefault(property =>
            property.AttributeProvider is MemberInfo info ? info.Name == memberName : property.Name == name);
        if (property is null || property.Get is null || property.IsExtensionData)
        {
            serializationInfo = null!;
            return false;
        }
        JsonSerializerOptions options = TypeInfo.Options;
        if (property.CustomConverter is not null || property.NumberHandling is not null)
        {
            options = new JsonSerializerOptions(options);
            if (property.CustomConverter is not null) options.Converters.Insert(0, property.CustomConverter);
            if (property.NumberHandling is { } handling) options.NumberHandling = handling;
        }
        IBsonSerializer serializer = _serializer(property.PropertyType, options);
        serializationInfo = _members.GetOrAdd(memberName, new BsonSerializationInfo(property.Name, serializer, property.PropertyType));
        return true;
    }
}
