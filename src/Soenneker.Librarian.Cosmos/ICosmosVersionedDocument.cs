namespace Soenneker.Librarian.Cosmos;

/// <summary>Opt-in support for keeping a typed document's version synchronized with Cosmos response headers.</summary>
public interface ICosmosVersionedDocument
{
    /// <summary>Gets or sets the authoritative response ETag. Map this property to the Cosmos _etag JSON field
    /// to populate it during queries and bulk reads. Successful point reads and writes refresh it directly.</summary>
    string? ETag { get; set; }
}
