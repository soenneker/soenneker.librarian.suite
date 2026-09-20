using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuditRow), TypeInfoPropertyName = "AuditRow")]
[JsonSerializable(typeof(FieldRow), TypeInfoPropertyName = "FieldRow")]
[JsonSerializable(typeof(CountedRow), TypeInfoPropertyName = "CountedRow")]
[JsonSerializable(typeof(ExampleDocument), TypeInfoPropertyName = "ExampleDocument")]
[JsonSerializable(typeof(IndexRow), TypeInfoPropertyName = "IndexRow")]
[JsonSerializable(typeof(OtherRow), TypeInfoPropertyName = "OtherRow")]
[JsonSerializable(typeof(PlannerRow), TypeInfoPropertyName = "PlannerRow")]
[JsonSerializable(typeof(PostgresRow), TypeInfoPropertyName = "PostgresRow")]
[JsonSerializable(typeof(QueryableRow), TypeInfoPropertyName = "QueryableRow")]
[JsonSerializable(typeof(RedisRow), TypeInfoPropertyName = "RedisRow")]
[JsonSerializable(typeof(RedisStatus), TypeInfoPropertyName = "RedisStatus")]
[JsonSerializable(typeof(IncrementalBatchTests.Row), TypeInfoPropertyName = "IncrementalBatchTestsRow")]
[JsonSerializable(typeof(QueryBufferTests.WideRow), TypeInfoPropertyName = "QueryBufferTestsWideRow")]
[JsonSerializable(typeof(QueryConformanceTests.ConformanceRow), TypeInfoPropertyName = "QueryConformanceTestsConformanceRow")]
[JsonSerializable(typeof(PostgresExpandedQueryTests.NumericRow), TypeInfoPropertyName = "PostgresExpandedQueryTestsNumericRow")]
internal partial class TestJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    internal static void Register()
    {
        LibrarianJson.Register(Default.AuditRow);
        LibrarianJson.Register(Default.FieldRow);
        LibrarianJson.Register(Default.CountedRow);
        LibrarianJson.Register(Default.ExampleDocument);
        LibrarianJson.Register(Default.IndexRow);
        LibrarianJson.Register(Default.OtherRow);
        LibrarianJson.Register(Default.PlannerRow);
        LibrarianJson.Register(Default.PostgresRow);
        LibrarianJson.Register(Default.QueryableRow);
        LibrarianJson.Register(Default.RedisRow);
        LibrarianJson.Register(Default.RedisStatus);
        LibrarianJson.Register(Default.IncrementalBatchTestsRow);
        LibrarianJson.Register(Default.QueryBufferTestsWideRow);
        LibrarianJson.Register(Default.QueryConformanceTestsConformanceRow);
        LibrarianJson.Register(Default.PostgresExpandedQueryTestsNumericRow);
    }
}
