using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

[JsonSerializable(typeof(PascalCaseRow))]
internal partial class PascalCaseJsonContext : JsonSerializerContext;
