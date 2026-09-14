using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Soenneker.Utils.Json;
using Soenneker.Utils.PooledStringBuilders;

namespace Soenneker.Librarian.Redis;

internal static class RedisIndexValue
{
    // Keep key segments readable while protecting separators, SORT patterns, and lexicographic ID bounds.
    internal static string KeySegment(string text)
    {
        var builder = new PooledStringBuilder(text.Length);
        try
        {
            Span<char> hex = stackalloc char[4];
            foreach (char character in text)
            {
                if (char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
                    builder.Append(character);
                else
                {
                    builder.Append('%');
                    ((int)character).TryFormat(hex, out _, "X4", CultureInfo.InvariantCulture);
                    builder.Append(hex);
                }
            }
            return builder.ToString();
        }
        finally { builder.Dispose(); }
    }

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
        for (int i = 0; i < text.Length; i++)
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
        JsonElement? element = JsonUtil.SerializeToElement(value);
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

    // Fixed-width decimal encoding keeps the exact decimal order in Redis lexicographic sorted sets.
    private static string Number(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger integer = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        integer *= BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 255));
        if (bits[3] < 0) integer = -integer;
        return "2" + (integer + BigInteger.Pow(10, 57)).ToString("D58", CultureInfo.InvariantCulture);
    }

    internal static string? Read(JsonElement document, string path)
    {
        foreach (string segment in path.Split('.'))
        {
            if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty(segment, out document)) return null;
        }
        return Encode(document);
    }

    internal static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        foreach (string segment in path.Split('.'))
            if (segment.Length == 0) throw new ArgumentException("Index paths cannot contain empty segments.", nameof(path));
    }
}
