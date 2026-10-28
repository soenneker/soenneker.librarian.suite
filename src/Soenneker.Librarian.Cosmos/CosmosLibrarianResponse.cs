using System.Net;

namespace Soenneker.Librarian.Cosmos;

/// <summary>Cosmos response metadata for diagnostics, request-unit accounting, and session propagation.</summary>
public sealed record CosmosLibrarianResponse(string ContainerName, string Operation, HttpStatusCode StatusCode,
    double RequestCharge, string? ActivityId, string? SessionToken, string? ETag, string? Diagnostics);
