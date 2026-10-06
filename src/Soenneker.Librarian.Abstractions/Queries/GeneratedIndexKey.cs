namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>A generated scalar index key: null (0), boolean (1), number (2), or text (3).</summary>
public readonly record struct GeneratedIndexKey(int Kind, decimal Number = 0, string? Text = null);
