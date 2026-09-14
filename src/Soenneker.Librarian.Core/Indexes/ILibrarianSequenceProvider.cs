using System.Collections.Generic;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Core.Indexes;

internal interface ILibrarianSequenceProvider
{
    IEnumerable<TElement> ExecuteSequence<TElement>(Expression expression);
}
