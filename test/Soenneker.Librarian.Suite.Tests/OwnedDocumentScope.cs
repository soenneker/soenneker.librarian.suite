using System.Linq;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class OwnedDocumentScope : ILibrarianRepositoryScope<ExampleDocument>
{
    public IQueryable<ExampleDocument> Apply(IQueryable<ExampleDocument> query) => query.Where(document => document.Name == "owned");
    public bool Contains(ExampleDocument document) => document.Name == "owned";
}
