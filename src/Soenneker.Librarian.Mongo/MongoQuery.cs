using System;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Soenneker.Librarian.Mongo;

internal static class MongoQuery
{
    internal static FilterDefinition<BsonDocument> Filter(MongoQueryFilter filter)
    {
        var builder = Builders<BsonDocument>.Filter;
        switch (filter.Operation)
        {
            case "all": return builder.Empty;
            case "none": return builder.Exists("_id", false);
            case "and": return Filter(filter.Left!) & Filter(filter.Right!);
            case "or": return Filter(filter.Left!) | Filter(filter.Right!);
            case "not": return builder.Not(Filter(filter.Left!));
            case "in":
                FilterDefinition<BsonDocument> choices = builder.Exists("_id", false);
                foreach (string value in filter.Values!) choices |= Filter(new MongoQueryFilter("term", filter.Path, "[" + value + "!", "[" + value + "!~"));
                return choices;
            case "term":
                string field = "values." + MongoDocument.Field(filter.Path!);
                FilterDefinition<BsonDocument> result = builder.Exists(field);
                if (filter.Minimum != "-") result &= filter.Minimum[0] == '[' ? builder.Gte(field, filter.Minimum[1..]) : builder.Gt(field, filter.Minimum[1..]);
                if (filter.Maximum != "+") result &= filter.Maximum[0] == '[' ? builder.Lte(field, filter.Maximum[1..]) : builder.Lt(field, filter.Maximum[1..]);
                return result;
            default: throw new NotSupportedException("Unsupported MongoDB query filter.");
        }
    }
}
