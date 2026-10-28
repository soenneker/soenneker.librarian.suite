using System.Threading;
using System.Threading.Tasks;
using Soenneker.Documents.Document;

namespace Soenneker.Librarian.Abstractions;

/// <summary>Prepares repository writes and observes confirmed commits for application metadata and auditing.</summary>
/// <remarks>BeforeAdd may assign a missing identity; hooks otherwise preserve identity. Before hooks may change document fields; update hooks may repeat on confirmed mutation
/// conflicts and must not produce external side effects. After hooks run once per confirmed write, with no caller
/// cancellation, and are not transactional with storage. An after-hook failure means the write has already committed;
/// do not blindly retry it. Hooks must be thread-safe when concurrent bulk writes are enabled. Direct container and
/// database batch operations bypass repository hooks; use an outbox when durable atomic auditing is required.</remarks>
public interface ILibrarianRepositoryLifecycle<TDocument> where TDocument : Document
{
    /// <summary>Prepares a new document before persistence.</summary>
    ValueTask BeforeAdd(TDocument document, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>Observes a confirmed addition.</summary>
    ValueTask AfterAdd(TDocument document) => ValueTask.CompletedTask;
    /// <summary>Prepares a replacement before persistence.</summary>
    ValueTask BeforeUpdate(TDocument document, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>Observes a confirmed replacement, including conditional updates and mutations.</summary>
    ValueTask AfterUpdate(TDocument document) => ValueTask.CompletedTask;
    /// <summary>Validates a deletion before persistence. Changes to the document are not persisted.</summary>
    ValueTask BeforeDelete(TDocument document, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>Observes a confirmed deletion with the last read document.</summary>
    ValueTask AfterDelete(TDocument document) => ValueTask.CompletedTask;
}
