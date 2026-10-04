using System;

namespace Soenneker.Librarian.CouchDb;

/// <summary>Connection and lazy provisioning settings for CouchDB.</summary>
public sealed class CouchDbLibrarianOptions
{
    /// <summary>The CouchDB server URL, optionally including a reverse-proxy path.</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>Optional HTTP Basic authentication username.</summary>
    public string? Username { get; init; }
    /// <summary>HTTP Basic authentication password. Supply through secret configuration.</summary>
    public string? Password { get; init; }
    /// <summary>The case-sensitive logical database namespace.</summary>
    public string Key { get; init; } = "librarian";
    /// <summary>A lowercase ASCII database-name prefix, starting with a letter.</summary>
    public string DatabasePrefix { get; init; } = "librarian";
    /// <summary>Creates missing physical databases on first access. Otherwise they must already exist.</summary>
    public bool EnsureDatabaseOnFirstUse { get; init; } = true;
}
