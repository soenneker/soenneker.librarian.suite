using System;
using System.Text.Json;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Core.Indexes;

internal readonly record struct IndexKey(int Kind, decimal Number = 0, string? Text = null) : IComparable<IndexKey>
{
    internal static IndexKey FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => new IndexKey(0),
        JsonValueKind.False => new IndexKey(1),
        JsonValueKind.True => new IndexKey(1, 1),
        JsonValueKind.Number when value.TryGetDecimal(out decimal number) => new IndexKey(2, number),
        JsonValueKind.String => new IndexKey(3, Text: value.GetString()),
        _ => throw new ArgumentException("Indexed values must be JSON null, booleans, decimal-compatible numbers, or strings.")
    };

    internal static IndexKey FromValue(object? value) => value switch
    {
        null => new IndexKey(0),
        string text => new IndexKey(3, Text: text),
        bool boolean => new IndexKey(1, boolean ? 1 : 0),
        decimal number => new IndexKey(2, number),
        int number => new IndexKey(2, number),
        long number => new IndexKey(2, number),
        uint number => new IndexKey(2, number),
        ulong number => new IndexKey(2, number),
        short number => new IndexKey(2, number),
        ushort number => new IndexKey(2, number),
        byte number => new IndexKey(2, number),
        sbyte number => new IndexKey(2, number),
        _ => FromJson(LibrarianJson.Element(value))
    };

    public int CompareTo(IndexKey other)
    {
        int kind = Kind.CompareTo(other.Kind);
        if (kind != 0)
            return kind;
        return Kind == 3 ? string.Compare(Text, other.Text, StringComparison.Ordinal) : Number.CompareTo(other.Number);
    }
}
