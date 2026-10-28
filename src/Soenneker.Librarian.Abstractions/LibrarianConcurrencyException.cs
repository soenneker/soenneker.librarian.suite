using System;

namespace Soenneker.Librarian.Abstractions;

/// <summary>A repository write encountered a confirmed version conflict. No write from that attempt was committed.</summary>
public sealed class LibrarianConcurrencyException(string message) : InvalidOperationException(message);
