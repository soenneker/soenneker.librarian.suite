using System;

namespace Soenneker.Librarian.Abstractions;

/// <summary>A mutation exhausted its retries because another writer kept changing the document.</summary>
public sealed class LibrarianConcurrencyException(string message) : InvalidOperationException(message);
