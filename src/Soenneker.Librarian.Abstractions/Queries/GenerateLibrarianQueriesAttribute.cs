using System;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Enables generated delegates for supported LINQ calls in this class.</summary>
/// <remarks>Unsupported lambdas retain their runtime behavior and produce a generator diagnostic.
/// This does not guarantee reflection-free execution of the entire query or its provider.</remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GenerateLibrarianQueriesAttribute : Attribute;
