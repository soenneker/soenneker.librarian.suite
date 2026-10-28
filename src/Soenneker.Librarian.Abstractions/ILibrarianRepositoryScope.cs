using System.Linq;
using Soenneker.Documents.Document;

namespace Soenneker.Librarian.Abstractions;

/// <summary>Defines an immutable document scope for a repository sharing a physical container.</summary>
/// <remarks>Apply and Contains must describe the same discriminator or tenant predicate. Apply must retain the
/// supplied query provider and add a server-translatable filter. This is repository isolation, not a security boundary
/// against callers with direct database access. Scoped writes require provider version support.</remarks>
public interface ILibrarianRepositoryScope<TDocument> where TDocument : Document
{
    /// <summary>Restricts the supplied query before projections, ordering, counts, or paging.</summary>
    IQueryable<TDocument> Apply(IQueryable<TDocument> query);

    /// <summary>Checks membership of a materialized document using the same predicate as Apply.</summary>
    bool Contains(TDocument document);
}
