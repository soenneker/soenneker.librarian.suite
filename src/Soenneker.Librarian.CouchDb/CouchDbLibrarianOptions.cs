using System;

namespace Soenneker.Librarian.CouchDb;

/// <summary>Connection and lazy provisioning settings for CouchDB.</summary>
public sealed class CouchDbLibrarianOptions
{
    /// <summary>The CouchDB server URL, optionally including a reverse-proxy path. Remote endpoints require HTTPS by default.</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>Allows plaintext HTTP to non-loopback endpoints, exposing credentials and documents on the connection.
    /// Disabled by default; loopback HTTP is allowed for local development.</summary>
    public bool AllowInsecureHttp { get; init; }
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
