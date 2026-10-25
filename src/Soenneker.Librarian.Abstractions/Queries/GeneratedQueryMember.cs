using System;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Compile-time member metadata shared by Librarian query providers. JsonName is an explicit name override;
/// null means the registered JSON contract determines the name.</summary>
public sealed record GeneratedQueryMember(string? JsonName, Func<object, object?> Get, bool CanIndex, Func<object, GeneratedIndexKey>? IndexKey);
