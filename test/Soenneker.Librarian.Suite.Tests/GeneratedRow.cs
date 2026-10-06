using Soenneker.Librarian.Abstractions.Queries;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Soenneker.Librarian.Suite.Tests;

[LibrarianModel]
public sealed class GeneratedRow
{
    [JsonPropertyName("age_value")]
    public int Age { get; set; }
    public string Name { get; set; } = "";
    public string? Note { get; set; }
    public int Computed => Age + 1;
    public int? Optional { get; set; }
    public int[] Scores { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
}
