namespace Soenneker.Librarian.Cosmos;

/// <summary>Observes Cosmos responses without retaining disposable SDK response objects.</summary>
public interface ICosmosLibrarianObserver
{
    /// <summary>Receives each response, including non-success stream responses and each query page.
    /// Implementations must be thread-safe and should return promptly. No document contents are provided.</summary>
    void OnResponse(CosmosLibrarianResponse response);
}
