using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Soenneker.Librarian.Core.Indexes;

// The owning container holds its AsyncLock for all access.
internal sealed class DocumentIndex
{
    private readonly string[] _segments;
    private readonly Dictionary<IndexKey, int> _counts = new();
    private readonly Dictionary<string, DocumentIndexNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private DocumentIndexNode? _root;
    private uint _random = 0x9E3779B9;

    internal DocumentIndex(string fieldPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPath);
        _segments = fieldPath.Split('.');
        foreach (string segment in _segments)
            if (string.IsNullOrWhiteSpace(segment))
                throw new ArgumentException("Index paths must contain nonempty JSON property names.", nameof(fieldPath));
    }

    internal IndexKey? Extract(JsonElement root)
    {
        JsonElement current = root;
        foreach (string segment in _segments)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return IndexKey.FromJson(current);
    }

    internal void Set(string id, IndexKey? key)
    {
        _nodes.TryGetValue(id, out DocumentIndexNode? node);
        if (node is not null)
        {
            if (key == node.Key)
                return;
            Detach(node);
            if (key is null)
                _nodes.Remove(id);
        }
        if (key is not { } actual)
            return;
        if (node is null)
        {
            node = new DocumentIndexNode(actual, id, NextPriority());
            _nodes.Add(id, node);
        }
        else
        {
            node.Key = actual;
            node.Left = node.Right = node.Previous = node.Next = null;
            node.Size = 1;
        }
        CollectionsMarshal.GetValueRefOrAddDefault(_counts, actual, out _)++;

        DocumentIndexNode? previous = null;
        DocumentIndexNode? next = null;
        _root = Insert(_root, node, ref previous, ref next);
        node.Previous = previous;
        node.Next = next;
        if (previous is not null) previous.Next = node;
        if (next is not null) next.Previous = node;
    }

    internal void Remove(string id)
    {
        if (_nodes.Remove(id, out DocumentIndexNode? node))
            Detach(node);
    }

    private void Detach(DocumentIndexNode node)
    {
        int count = _counts[node.Key];
        if (count == 1) _counts.Remove(node.Key);
        else _counts[node.Key] = count - 1;
        if (node.Previous is not null) node.Previous.Next = node.Next;
        if (node.Next is not null) node.Next.Previous = node.Previous;
        _root = Delete(_root!, node);
    }

    internal int Count(IndexKey key) => _counts.GetValueOrDefault(key);

    internal bool TryGetKey(string id, out IndexKey key)
    {
        if (_nodes.TryGetValue(id, out DocumentIndexNode? node)) { key = node.Key; return true; }
        key = default;
        return false;
    }

    internal int Count(IndexFilter filter) => filter.Minimum is { } key && filter.Maximum == key && filter.IncludeMinimum && filter.IncludeMaximum
        ? Count(key) : CountRange(filter.Minimum, filter.Maximum, filter.IncludeMinimum, filter.IncludeMaximum);

    // Enumerated only while the owning container holds its mutation gate.
    internal CandidateEnumerator Candidates(IndexFilter filter, bool descending)
    {
        int lower = filter.Minimum is { } min ? Rank(min, !filter.IncludeMinimum) : 0;
        int upper = filter.Maximum is { } max ? Rank(max, filter.IncludeMaximum) : Size(_root);
        return new CandidateEnumerator(At(descending ? upper - 1 : lower), upper - lower, descending);
    }

    internal struct CandidateEnumerator(DocumentIndexNode? next, int remaining, bool descending)
    {
        public string Current { get; private set; } = null!;
        public readonly CandidateEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (remaining <= 0) return false;
            Current = next!.Id;
            next = descending ? next.Previous : next.Next;
            remaining--;
            return true;
        }
    }

    internal int CountRange(IndexKey? minimum, IndexKey? maximum, bool includeMinimum = true, bool includeMaximum = true)
        => Math.Max(0, (maximum is { } hi ? Rank(hi, includeMaximum) : Size(_root)) - (minimum is { } lo ? Rank(lo, !includeMinimum) : 0));

    internal IndexPage Equal(IndexKey key, Dictionary<string, string> items, int skip, int take, CancellationToken token)
    {
        int count = Count(key);
        if (skip >= count) return IndexPage.Empty;
        int start = Rank(key, false) + skip;
        return Capture(start, Math.Min(count - skip, take), false, items, token);
    }

    internal IndexPage Range(IndexKey? minimum, IndexKey? maximum, bool descending,
        Dictionary<string, string> items, int skip, int take, CancellationToken token, bool includeMinimum = true, bool includeMaximum = true)
    {
        if (minimum is { } min && maximum is { } max)
        {
            if (min.Kind != max.Kind)
                throw new ArgumentException("Range bounds must have the same JSON scalar type.");
            if (min.CompareTo(max) > 0)
                throw new ArgumentException("The minimum must not exceed the maximum.");
        }
        int lower = minimum is { } lo ? Rank(lo, !includeMinimum) : 0;
        int upper = maximum is { } hi ? Rank(hi, includeMaximum) : Size(_root);
        int count = upper - lower;
        if (skip >= count) return IndexPage.Empty;
        int start = descending ? upper - skip - 1 : lower + skip;
        return Capture(start, Math.Min(count - skip, take), descending, items, token);
    }

    private IndexPage Capture(int rank, int count, bool descending, Dictionary<string, string> items, CancellationToken token)
    {
        string[] buffer = ArrayPool<string>.Shared.Rent(count);
        var written = 0;
        try
        {
            DocumentIndexNode? node = At(rank);
            while (written < count)
            {
                token.ThrowIfCancellationRequested();
                buffer[written++] = items[node!.Id];
                node = descending ? node.Previous : node.Next;
            }
            return new IndexPage(buffer, count);
        }
        catch
        {
            Array.Clear(buffer, 0, written);
            ArrayPool<string>.Shared.Return(buffer);
            throw;
        }
    }

    // Number of entries strictly before the key, or through the key when inclusive.
    private int Rank(IndexKey key, bool inclusive)
    {
        var rank = 0;
        DocumentIndexNode? node = _root;
        while (node is not null)
        {
            int comparison = node.Key.CompareTo(key);
            if (comparison < 0 || (inclusive && comparison == 0))
            {
                rank += Size(node.Left) + 1;
                node = node.Right;
            }
            else node = node.Left;
        }
        return rank;
    }

    private DocumentIndexNode? At(int rank)
    {
        DocumentIndexNode? node = _root;
        while (node is not null)
        {
            int left = Size(node.Left);
            if (rank == left) return node;
            if (rank < left) node = node.Left;
            else { rank -= left + 1; node = node.Right; }
        }
        return null;
    }

    private static DocumentIndexNode Insert(DocumentIndexNode? root, DocumentIndexNode node,
        ref DocumentIndexNode? previous, ref DocumentIndexNode? successor)
    {
        if (root is null) return node;
        if (Compare(node, root) < 0)
        {
            successor = root;
            root.Left = Insert(root.Left, node, ref previous, ref successor);
            if (root.Left.Priority < root.Priority)
            {
                DocumentIndexNode next = root.Left;
                root.Left = next.Right;
                next.Right = root;
                Refresh(root);
                root = next;
            }
        }
        else
        {
            previous = root;
            root.Right = Insert(root.Right, node, ref previous, ref successor);
            if (root.Right.Priority < root.Priority)
            {
                DocumentIndexNode next = root.Right;
                root.Right = next.Left;
                next.Left = root;
                Refresh(root);
                root = next;
            }
        }
        Refresh(root);
        return root;
    }

    private static DocumentIndexNode? Delete(DocumentIndexNode root, DocumentIndexNode target)
    {
        int comparison = Compare(target, root);
        if (comparison == 0) return Merge(root.Left, root.Right);
        if (comparison < 0) root.Left = Delete(root.Left!, target);
        else root.Right = Delete(root.Right!, target);
        Refresh(root);
        return root;
    }

    private static DocumentIndexNode? Merge(DocumentIndexNode? left, DocumentIndexNode? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        if (left.Priority < right.Priority)
        {
            left.Right = Merge(left.Right, right);
            Refresh(left);
            return left;
        }
        right.Left = Merge(left, right.Left);
        Refresh(right);
        return right;
    }

    private static int Compare(DocumentIndexNode left, DocumentIndexNode right)
    {
        int key = left.Key.CompareTo(right.Key);
        return key != 0 ? key : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
    }

    private uint NextPriority()
    {
        _random ^= _random << 13;
        _random ^= _random >> 17;
        _random ^= _random << 5;
        return _random;
    }

    private static int Size(DocumentIndexNode? node) => node?.Size ?? 0;
    private static void Refresh(DocumentIndexNode node) => node.Size = Size(node.Left) + Size(node.Right) + 1;

    internal void Clear()
    {
        _root = null;
        _counts.Clear();
        _nodes.Clear();
    }

}
