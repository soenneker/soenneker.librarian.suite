using System;

namespace Soenneker.Librarian.R2;

/// <summary>An R2 object's bytes and the ETag read with those bytes.</summary>
public sealed record R2LibrarianObject(ReadOnlyMemory<byte> Content, string ETag);
