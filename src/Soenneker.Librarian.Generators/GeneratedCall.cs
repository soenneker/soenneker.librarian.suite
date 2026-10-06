using Microsoft.CodeAnalysis;

namespace Soenneker.Librarian.Generators;

internal sealed class GeneratedCall(Location location, string? error, string? source, bool explicitOptIn = true)
{
    internal bool Explicit { get; } = explicitOptIn;
    internal Location Location { get; } = location;
    internal string? Error { get; } = error;
    internal string? Source { get; } = source;
}
