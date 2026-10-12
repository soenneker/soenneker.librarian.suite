using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Remote.Tests;

[JsonSerializable(typeof(RemoteRow))]
internal partial class RemoteJsonContext : JsonSerializerContext;
