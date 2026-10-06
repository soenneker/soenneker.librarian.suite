using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Memory;

namespace Soenneker.Librarian.Suite.Tests;

// Neither the model nor this class opts in through attributes.
public sealed class AutomaticQueryTests
{
    [Test]
    public async Task Librarian_queries_generate_metadata_and_delegates_without_attributes()
    {
        LibrarianJson.Register(GeneratedRowJsonContext.Default.AutomaticQueryRow);
        if (!GeneratedQueryMetadata.TryGet(typeof(AutomaticQueryRow), "Age", out var metadata) || !metadata.CanIndex)
            throw new InvalidOperationException("The model was not discovered from BuildQueryable.");
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        var container = await database.GetContainer("automatic");
        await container.AddItem("1", "{\"age\":10,\"name\":\"Zoe\"}");
        await container.AddItem("2", "{\"age\":30,\"name\":\"Amy\"}");
        foreach (bool adultsOnly in new[] { true, false })
        {
            var query = container.BuildQueryable<AutomaticQueryRow>();
            if (adultsOnly) query = query.Where(row => row.Age >= 18);
            var alias = query;
            var names = alias.OrderBy(row => row.Name).Select(row => row.Name);
            var selector = (LambdaExpression)((UnaryExpression)((MethodCallExpression)names.Expression).Arguments[1]).Operand;
            if (!GeneratedQueryFunctions.TryGet(selector, out _))
                throw new InvalidOperationException("The composed query was not discovered.");
            if (!names.SequenceEqual(adultsOnly ? new[] { "Amy" } : new[] { "Amy", "Zoe" }))
                throw new InvalidOperationException("Automatic generation changed query results.");
        }
        int minimum = 18;
        var captured = container.BuildQueryable<AutomaticQueryRow>().Where(row => row.Age >= minimum);
        if (captured.Count() != 1) throw new InvalidOperationException("Capture fallback failed.");
        minimum = 40;
        if (captured.Count() != 0) throw new InvalidOperationException("The captured value was evaluated too early.");
    }
}
