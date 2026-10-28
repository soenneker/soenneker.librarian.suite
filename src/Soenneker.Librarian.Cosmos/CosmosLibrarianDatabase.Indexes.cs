using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;

namespace Soenneker.Librarian.Cosmos;

public sealed partial class CosmosLibrarianDatabase
{
    /// <summary>Adds scalar and composite indexes to a container's existing consistent indexing policy.
    /// Scalar paths use dot-separated JSON names; composite paths use Cosmos slash syntax.</summary>
    /// <remarks>Explicit administrative operation. Preserves unrelated settings, uses ETag concurrency, and retries
    /// confirmed conflicts up to five times. Refuses disabled indexing and conflicting exact exclusions rather than
    /// silently changing their meaning. Index transformation may continue after this call completes.</remarks>
    public async ValueTask EnsureIndexes(string containerName, IReadOnlyList<string> scalarPaths,
        IReadOnlyList<IReadOnlyList<CompositePath>>? compositeIndexes = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(scalarPaths);
        string[] paths = scalarPaths.Select(IndexPath).Distinct(StringComparer.Ordinal).ToArray();
        var composites = new List<Collection<CompositePath>>();
        if (compositeIndexes is not null)
            foreach (IReadOnlyList<CompositePath> composite in compositeIndexes)
            {
                if (composite.Count < 2) throw new ArgumentException("A composite index needs at least two paths.", nameof(compositeIndexes));
                var copy = new Collection<CompositePath>();
                foreach (CompositePath path in composite)
                {
                    if (string.IsNullOrWhiteSpace(path.Path) || !path.Path.StartsWith('/') || path.Path.Contains('*') || path.Path.Contains('?'))
                        throw new ArgumentException("Composite paths must be absolute scalar paths without wildcards.", nameof(compositeIndexes));
                    copy.Add(new CompositePath { Path = path.Path, Order = path.Order });
                }
                composites.Add(copy);
            }
        if (paths.Length == 0 && composites.Count == 0) return;
        Container store;
        using (await _gate.Acquire(cancellationToken).NoSync())
            store = await Store(containerName, cancellationToken).NoSync();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Check(cancellationToken);
            ContainerResponse current = await store.ReadContainerAsync(cancellationToken: cancellationToken).NoSync();
            Observe(containerName, "ReadIndexPolicy", current.StatusCode, current.Headers, current.Diagnostics);
            ContainerProperties properties = current.Resource;
            IndexingPolicy policy = properties.IndexingPolicy;
            if (policy.IndexingMode != IndexingMode.Consistent || !policy.Automatic)
                throw new InvalidOperationException("Explicit index management requires automatic consistent indexing.");
            bool changed = false;
            foreach (string path in paths)
            {
                if (policy.ExcludedPaths.Any(excluded => excluded.Path == path))
                    throw new InvalidOperationException($"Index path '{path}' is explicitly excluded.");
                if (policy.IncludedPaths.Any(included => included.Path == path)) continue;
                policy.IncludedPaths.Add(new IncludedPath { Path = path });
                changed = true;
            }
            foreach (Collection<CompositePath> composite in composites)
            {
                if (policy.CompositeIndexes.Any(existing => existing.Count == composite.Count && existing.Zip(composite)
                    .All(pair => pair.First.Path == pair.Second.Path && pair.First.Order == pair.Second.Order))) continue;
                policy.CompositeIndexes.Add(composite);
                changed = true;
            }
            if (!changed) return;
            try
            {
                ContainerResponse updated = await store.ReplaceContainerAsync(properties,
                    new ContainerRequestOptions { IfMatchEtag = current.ETag }, cancellationToken).NoSync();
                Observe(containerName, "UpdateIndexPolicy", updated.StatusCode, updated.Headers, updated.Diagnostics);
                return;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 4) { }
        }
    }

    private static string IndexPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string[] segments = path.Split('.');
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment.Any(character => !char.IsLetterOrDigit(character) && character != '_')))
            throw new ArgumentException("Index paths require dot-separated property names containing letters, digits, or underscores.", nameof(path));
        return "/" + string.Join('/', segments) + "/?";
    }
}
