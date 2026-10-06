using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Soenneker.Librarian.Mongo;
namespace Soenneker.Librarian.Suite.Tests;
public sealed class MongoSerializerRegistryTests
{
    [Test]
    public void Explicit_members_preserve_json_names_and_number_handling()
    {
        var registry = new MongoJsonSerializerRegistry();
        registry.RegisterScalar<int>();
        registry.RegisterDocument<MongoLinqRow>(new Dictionary<string, string>
        {
            [nameof(MongoLinqRow.Score)] = "score_value",
            [nameof(MongoLinqRow.StringNumber)] = "stringNumber"
        });
        var serializer = (IBsonDocumentSerializer)registry.Create(typeof(MongoLinqRow), TestJsonContext.Default.Options);
        if (!serializer.TryGetMemberSerializationInfo(nameof(MongoLinqRow.Score), out var score) || score.ElementName != "score_value")
            throw new Exception("Explicit JSON property name was lost.");
        if (!serializer.TryGetMemberSerializationInfo(nameof(MongoLinqRow.StringNumber), out var number) ||
            ((IHasRepresentationSerializer)number.Serializer).Representation != BsonType.String)
            throw new Exception("Member number handling was lost.");
        if (serializer.TryGetMemberSerializationInfo("Unregistered", out _)) throw new Exception("Unknown member accepted.");
    }
    [Test]
    public void Unregistered_serializers_fail_without_runtime_discovery()
    {
        try { new MongoJsonSerializerRegistry().Create(typeof(NativeDocument), TestJsonContext.Default.Options); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Missing serializer was not rejected.");
    }
}
