using System;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Compile-time member metadata shared by Librarian query providers.</summary>
public sealed record GeneratedQueryMember(string JsonName, Func<object, object?> Get, bool CanIndex, Func<object, GeneratedIndexKey>? IndexKey);
