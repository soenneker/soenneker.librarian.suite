using System.Collections.Generic;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Preserves deferred sequence execution when a provider supports streaming enumeration.</summary>
public interface ILibrarianSequenceProvider
{
    /// <summary>Executes a sequence without forcing terminal materialization.</summary>
    IEnumerable<TElement> ExecuteSequence<TElement>(Expression expression);
}
