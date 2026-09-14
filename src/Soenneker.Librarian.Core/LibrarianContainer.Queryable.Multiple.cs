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
        var filters = new IndexFilter[length];
        var indexes = new DocumentIndex[length];
        filters[0] = plan.PrimaryFilter;
        plan.AdditionalFilters?.CopyTo(filters, 1);
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
        if (smallest == 0) return IndexPage.Empty;

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
        List<string>? matches = plan.CountOnly ? null : new List<string>(Math.Min(smallest, needsSort ? 256 : plan.Take));
        var countMatches = 0;
        foreach (string id in driverIndex.Candidates(driverFilter, !needsSort && plan.OrderProperty is not null && plan.Descending))
        {
            var match = true;
            for (var i = 0; i < length; i++)
            {
                if (i == driver) continue;
                if (!indexes[i].TryGetKey(id, out IndexKey key) || !filters[i].Matches(key)) { match = false; break; }
            }
            if (!match) continue;
            countMatches++;
            if (needsSort) matches!.Add(id);
            else if (countMatches > plan.Skip) matches?.Add(id);
            if (!needsSort && (long)countMatches >= (long)plan.Skip + plan.Take) break;
        }
        if (plan.CountOnly)
        {
            plan.Count = Math.Min(plan.Take, Math.Max(0, countMatches - plan.Skip));
            return IndexPage.Empty;
        }
        int skip = needsSort ? plan.Skip : 0;
        int take = Math.Min(plan.Take, Math.Max(0, matches!.Count - skip));
        if (take == 0) return IndexPage.Empty;
        if (needsSort)
        {
            matches.Sort((left, right) =>
            {
                orderIndex!.TryGetKey(left, out IndexKey a);
                orderIndex.TryGetKey(right, out IndexKey b);
                int comparison = a.CompareTo(b);
                if (comparison == 0) comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                return plan.Descending ? -comparison : comparison;
            });
        }
        string[] documents = ArrayPool<string>.Shared.Rent(take);
        for (var i = 0; i < take; i++) documents[i] = _items[matches[skip + i]];
        return new IndexPage(documents, take);
    }
}
