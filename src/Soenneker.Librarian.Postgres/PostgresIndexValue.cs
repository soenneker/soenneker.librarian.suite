using System;
using System.Globalization;
using System.Text.Json;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Utils.PooledStringBuilders;

namespace Soenneker.Librarian.Postgres;

internal static class PostgresIndexValue
{
    internal static string Hex(string text, string prefix = "")
    {
        var builder = new PooledStringBuilder(checked(prefix.Length + text.Length * 4));
        try
        {
            builder.Append(prefix);
            AppendHex(ref builder, text);
            return builder.ToString();
        }
        finally { builder.Dispose(); }
    }

    internal static void AppendHex(ref PooledStringBuilder builder, string text)
    {
        const string digits = "0123456789ABCDEF";
        Span<char> destination = builder.AppendSpan(checked(text.Length * 4));
        for (var i = 0; i < text.Length; i++)
        {
            char character = text[i];
            int offset = i * 4;
            destination[offset] = digits[character >> 12];
            destination[offset + 1] = digits[(character >> 8) & 15];
            destination[offset + 2] = digits[(character >> 4) & 15];
            destination[offset + 3] = digits[character & 15];
        }
    }

    internal static string Encode(object? value)
    {
        // Preserve the serializer fallback for enums and custom values, but avoid a JSON document for scalar keys.
        switch (value)
        {
            case null: return "0";
            case string text: return Hex(text, "3");
            case bool boolean: return boolean ? "11" : "10";
            case decimal number: return Number(number);
            case int number: return Number(number);
            case long number: return Number(number);
            case uint number: return Number(number);
            case ulong number: return Number(number);
            case short number: return Number(number);
            case ushort number: return Number(number);
            case byte number: return Number(number);
            case sbyte number: return Number(number);
        }
        JsonElement? element = LibrarianJson.Element(value);
        return element is { } scalar ? Encode(scalar) : "0";
    }

    internal static string Encode(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "0",
        JsonValueKind.False => "10",
        JsonValueKind.True => "11",
        JsonValueKind.String => Hex(value.GetString()!, "3"),
        JsonValueKind.Number when value.TryGetDecimal(out decimal number) => Number(number),
        _ => throw new ArgumentException("Indexed values must be null, booleans, decimal-compatible numbers, or strings.")
    };

    // Fixed-width decimal encoding keeps the exact decimal order in PostgreSQL C-collated B-tree indexes.
    private static string Number(decimal value)
    {
        return string.Create(59, value, static (destination, number) =>
        {
            // The stored format is 10^57 + number * 10^28, padded to 58 digits, prefixed by '2'.
            // Decimal's fixed-point formatter supplies the exact magnitude without BigInteger temporaries.
            destination.Fill('0');
            destination[0] = '2';
            destination[1] = '1';
            Span<char> digits = stackalloc char[59];
            decimal.Abs(number).TryFormat(digits, out int written, "F28", CultureInfo.InvariantCulture);
            int target = destination.Length - 1;
            for (int i = written - 1; i >= 0; i--)
                if (digits[i] != '.') destination[target--] = digits[i];
            if (number >= 0) return;
            // Subtract the magnitude from 10^57 using a decimal ten's complement.
            destination[1] = '0';
            var carry = 1;
            for (int i = destination.Length - 1; i >= 2; i--)
            {
                int digit = 9 - (destination[i] - '0') + carry;
                destination[i] = (char)('0' + digit % 10);
                carry = digit / 10;
            }
        });
    }

    internal static string? Read(JsonElement document, string path)
    {
        ReadOnlySpan<char> pathSpan = path.AsSpan();
        foreach (Range segment in pathSpan.Split('.'))
        {
            if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty(pathSpan[segment], out document)) return null;
        }
        return Encode(document);
    }

    internal static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] == '.' || path[^1] == '.' || path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Index paths cannot contain empty segments.", nameof(path));
    }
}
