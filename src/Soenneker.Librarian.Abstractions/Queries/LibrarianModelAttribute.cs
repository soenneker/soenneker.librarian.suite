using System;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Generates member accessors and serialized member names for a document type.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class LibrarianModelAttribute : Attribute;
