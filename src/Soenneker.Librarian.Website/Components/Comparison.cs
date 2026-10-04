namespace Soenneker.Librarian.Website.Components;

internal sealed record Comparison(string Label, Measurement Librarian, Measurement LiteDb, Measurement Sqlite);
