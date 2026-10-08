using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Memory;
using Soenneker.Librarian.Mongo;
using MongoDB.Bson.Serialization;
using Soenneker.Librarian.Postgres;
using Soenneker.Librarian.Redis;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

[GenerateLibrarianQueries]
public sealed class GeneratedQueryTests
{
    [Test]
    public void Generated_metadata_preserves_names_and_excludes_computed_indexes()
    {
        Check(GeneratedQueryMetadata.TryGet(typeof(GeneratedRow), "Age", out var age));
        Check(age.JsonName == "age_value" && age.CanIndex && (int)age.Get(new GeneratedRow { Age = 42 })! == 42);
        Check(GeneratedQueryMetadata.TryGet(typeof(GeneratedRow), "Computed", out var computed) && !computed.CanIndex);
        Expression<Func<GeneratedRow, int>> expression = row => row.Age;
        Check(QueryExpressionReader.Path(expression.Body, expression.Parameters[0], () => new NotSupportedException()) == "age_value");
    }

    [Test]
    public async Task Generated_lambdas_compose_through_branches_and_helpers(CancellationToken cancellationToken)
    {
        LibrarianJson.Register(GeneratedRowJsonContext.Default.GeneratedRow);
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("generated", cancellationToken: cancellationToken);
        await container.AddItem("1", "{\"age_value\":10,\"name\":\"Zoe\"}", cancellationToken: cancellationToken);
        await container.AddItem("2", "{\"age_value\":30,\"name\":\"Amy\"}", cancellationToken: cancellationToken);
        await container.AddItem("3", "{\"age_value\":40,\"name\":\"Bob\"}", cancellationToken: cancellationToken);
        foreach (bool filter in new[] { true, false })
        {
            IQueryable<GeneratedRow> query = container.BuildQueryable<GeneratedRow>();
            if (filter) query = query.Where(row => row.Age >= 20);
            query = Sort(query);
            var selected = query.Select(row => row.Name);
            var call = (MethodCallExpression)selected.Expression;
            var lambda = (LambdaExpression)((UnaryExpression)call.Arguments[1]).Operand;
            Check(GeneratedQueryFunctions.TryGet(lambda, out var generated));
            Check((string)generated(new GeneratedRow { Name = "direct" }, 0)! == "direct");
            Check(selected.SequenceEqual(filter ? new[] { "Amy", "Bob" } : new[] { "Amy", "Bob", "Zoe" }));
        }

        // Captures deliberately retain the runtime fallback and must remain live on every execution.
        int minimum = 20;
#pragma warning disable LIBGEN002
        var captured = container.BuildQueryable<GeneratedRow>().Where(row => row.Age >= minimum);
#pragma warning restore LIBGEN002
        Check(captured.Count() == 2);
        minimum = 40;
        Check(captured.Count() == 1);
    }

    private static IQueryable<GeneratedRow> Sort(IQueryable<GeneratedRow> query) => query.OrderBy(row => row.Name);

    [Test]
    public void Generated_projection_constructs_provider_columns()
    {
        var root = Array.Empty<GeneratedRow>().AsQueryable();
        var query = root.Take(2).Select(row => new GeneratedProjection(row.Age, row.Name));
        var lambda = (LambdaExpression)((UnaryExpression)((MethodCallExpression)query.Expression).Arguments[1]).Operand;
        Check(GeneratedQueryProjections.TryGet(lambda.Body, new[] { ("age_value", typeof(int)), ("name", typeof(string)) }, out var materialize));
        Check(materialize(new[] { "31", "\"Ada\"" }) is GeneratedProjection { Age: 31, Name: "Ada" });
        Check(PostgresQueryPlan.Create(query.Expression, root.Provider).Projection!.Materialize(new[] { "31", "\"Ada\"" }) is GeneratedProjection { Age: 31, Name: "Ada" });
        Check(RedisQueryPlan.Create(query.Expression, root.Provider).Projection!.Materialize(new[] { "31", "\"Ada\"" }) is GeneratedProjection { Age: 31, Name: "Ada" });
    }

    [Test]
    public void Generated_mongo_registration_uses_explicit_factories()
    {
        var registry = new MongoJsonSerializerRegistry();
        Soenneker.Librarian.Generated.LibrarianModels.RegisterMongo(registry);
        var serializer = (IBsonDocumentSerializer)registry.Create(typeof(GeneratedRow), GeneratedRowJsonContext.Default.Options);
        Check(serializer.TryGetMemberSerializationInfo("Age", out var age) && age.ElementName == "age_value");
        Check(serializer.TryGetMemberSerializationInfo("Optional", out var optional) && optional.Serializer.ValueType == typeof(int?));
        Check(serializer.TryGetMemberSerializationInfo("Scores", out var scores) && scores.Serializer is IBsonArraySerializer);
        Check(serializer.TryGetMemberSerializationInfo("Tags", out var tags) && tags.Serializer is IBsonArraySerializer);
        Check(serializer.TryGetMemberSerializationInfo("Counts", out var counts) && counts.Serializer is IBsonDictionarySerializer);
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Generated query assertion failed.");
    }
}
