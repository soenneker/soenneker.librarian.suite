using System;

namespace Soenneker.Librarian.R2;

/// <summary>The requested document already exists or a concurrent creator won its conditional write.</summary>
public sealed class R2LibrarianItemAlreadyExistsException(string id)
    : InvalidOperationException($"Document '{id}' already exists or was concurrently created.");
