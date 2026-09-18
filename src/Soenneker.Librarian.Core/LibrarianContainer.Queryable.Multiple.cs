using System;
using System.Buffers;
using System.Collections.Generic;
using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

public sealed partial class LibrarianContainer
{
    // Called under _mutationGate. Intersect index keys before accessing document JSON.
    private IndexPage QueryMultipleIndexes<T>(QueryPlan plan)
    {
        int length = 1 + (plan.AdditionalFilters?.Count ?? 0);
        InlineBuffer<IndexFilter> filterBuffer = default;
        InlineBuffer<DocumentIndex> indexBuffer = default;
        Span<IndexFilter> filters = length <= 8 ? ((Span<IndexFilter>)filterBuffer)[..length] : new IndexFilter[length];
        Span<DocumentIndex> indexes = length <= 8 ? indexBuffer : new DocumentIndex[length];
        filters[0] = plan.PrimaryFilter;
        if (plan.AdditionalFilters is { } additional)
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(additional).CopyTo(filters[1..]);
        foreach (IndexFilter filter in filters)
            if (_automaticIndexes.TryGetValue((typeof(T), filter.Property), out AutomaticIndex? existing)
                && existing.Index.Count(filter) == 0) return IndexPage.Empty;
        PrepareAutomaticIndexes<T>(filters, plan.CountOnly ? null : plan.OrderProperty);
        int driver = 0, smallest = int.MaxValue;
        for (var i = 0; i < length; i++)
        {
            indexes[i] = GetAutomaticIndex<T>(filters[i].Property).Index;
            int count = indexes[i].Count(filters[i]);
            if (count < smallest) { driver = i; smallest = count; }
        }
        if (smallest <= plan.Skip) return IndexPage.Empty;

        bool needsSort = !plan.CountOnly && plan.OrderProperty is { } order && order != filters[driver].Property;
        DocumentIndex? orderIndex = needsSort ? GetAutomaticIndex<T>(plan.OrderProperty!).Index : null;
        DocumentIndex driverIndex = indexes[driver];
        IndexFilter driverFilter = filters[driver];
        if (needsSort)
        {
            var orderedFilter = new IndexFilter(plan.OrderProperty!);
            int orderedDriver = -1;
            for (var i = 0; i < length; i++)
                if (filters[i].Property == plan.OrderProperty) { orderedFilter = filters[i]; orderedDriver = i; break; }
            // Broad predicates and small ordered pages are cheaper to stream in order than to collect and sort all matches.
            // Sparse predicates still start from the smallest candidate set.
            double expectedReads = ((double)plan.Skip + plan.Take) * orderIndex!.Count(orderedFilter) / smallest;
            if (expectedReads < smallest)
            {
                driver = orderedDriver;
                driverIndex = orderIndex;
                driverFilter = orderedFilter;
                needsSort = false;
            }
        }
        if (!needsSort) return CaptureMatches(plan, indexes[..length], filters, driver, driverIndex, driverFilter, smallest);
        var matches = new List<string>(Math.Min(smallest, 256));
        foreach (string id in driverIndex.Candidates(driverFilter, false))
            if (Matches(id, indexes[..length], filters, driver)) matches.Add(id);
        int take = Math.Min(plan.Take, Math.Max(0, matches.Count - plan.Skip));
        if (take == 0) return IndexPage.Empty;
        SortMatches(matches, orderIndex!, plan.Descending);
        string[] documents = ArrayPool<string>.Shared.Rent(take);
        for (var i = 0; i < take; i++) documents[i] = _items[matches[plan.Skip + i]];
        return new IndexPage(documents, take);
    }

    private static bool Matches(string id, ReadOnlySpan<DocumentIndex> indexes, ReadOnlySpan<IndexFilter> filters, int driver)
    {
        for (var i = 0; i < filters.Length; i++)
        {
            if (i == driver) continue;
            if (!indexes[i].TryGetKey(id, out IndexKey key) || !filters[i].Matches(key)) return false;
        }
        return true;
    }

    private IndexPage CaptureMatches(QueryPlan plan, ReadOnlySpan<DocumentIndex> indexes, ReadOnlySpan<IndexFilter> filters,
        int driver, DocumentIndex driverIndex, IndexFilter driverFilter, int smallest)
    {
        string[]? documents = plan.CountOnly ? null : ArrayPool<string>.Shared.Rent(Math.Min(smallest - plan.Skip, plan.Take));
        var countMatches = 0;
        var written = 0;
        try
        {
            foreach (string id in driverIndex.Candidates(driverFilter, plan.OrderProperty is not null && plan.Descending))
            {
                if (!Matches(id, indexes, filters, driver)) continue;
                countMatches++;
                if (countMatches > plan.Skip && documents is not null) documents[written++] = _items[id];
                if ((long)countMatches >= (long)plan.Skip + plan.Take) break;
            }
            plan.Count = Math.Min(plan.Take, Math.Max(0, countMatches - plan.Skip));
            if (written == 0) return IndexPage.Empty;
            var page = new IndexPage(documents!, written);
            documents = null; // Transfer ownership to the caller's page lease.
            return page;
        }
        finally
        {
            if (documents is not null)
            {
                Array.Clear(documents, 0, written);
                ArrayPool<string>.Shared.Return(documents);
            }
        }
    }

    private static void SortMatches(List<string> matches, DocumentIndex orderIndex, bool descending)
    {
        matches.Sort((left, right) =>
        {
            orderIndex.TryGetKey(left, out IndexKey a);
            orderIndex.TryGetKey(right, out IndexKey b);
            int comparison = a.CompareTo(b);
            if (comparison == 0) comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return descending ? -comparison : comparison;
        });
    }
}
