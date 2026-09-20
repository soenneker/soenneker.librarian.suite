using System;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Soenneker.Librarian.Redis;

namespace Soenneker.Librarian.Suite.Tests;

public class RedisEncodingTests
{
    private static string Encode(object? value) => RedisIndexValue.Encode(value);

    [Test]
    public void Decimal_encoding_matches_the_persisted_format_for_every_scale_and_extreme_values()
    {
        var random = new Random(99173);
        decimal[] extremes = [decimal.MinValue, decimal.MaxValue, 0, 1, -1, 0.0000000000000000000000000001m, -0.0000000000000000000000000001m];
        foreach (decimal value in extremes) Check(value);
        for (byte scale = 0; scale <= 28; scale++)
        {
            Check(new decimal(0, 0, 0, true, scale));
            for (var i = 0; i < 200; i++)
                Check(new decimal((int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1),
                    (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1),
                    (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1), i % 2 == 0, scale));
        }

        static void Check(decimal value)
        {
            int[] bits = decimal.GetBits(value);
            BigInteger magnitude = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
            magnitude *= BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 255));
            if (bits[3] < 0) magnitude = -magnitude;
            string expected = "2" + (magnitude + BigInteger.Pow(10, 57)).ToString("D58", CultureInfo.InvariantCulture);
            if (Encode(value) != expected) throw new Exception($"Stored decimal format changed for {value}.");
        }
    }

    [Test]
    public void Primitive_fast_paths_match_JSON_keys_and_safe_segments_are_reused()
    {
        object?[] values = [null, "", "a!*é😀", false, true, int.MinValue, long.MinValue, ulong.MaxValue,
            uint.MaxValue, short.MinValue, ushort.MaxValue, byte.MaxValue, sbyte.MinValue, 1.25m, 1.5d];
        foreach (object? value in values)
            if (Encode(value) != RedisIndexValue.Encode(JsonSerializer.SerializeToElement(value))) throw new Exception($"Scalar key mismatch: {value}");
        const string safe = "jobs.state-123_A";
        if (!ReferenceEquals(safe, RedisIndexValue.KeySegment(safe)) || RedisIndexValue.KeySegment(":%*") != "%003A%0025%002A") throw new Exception("Key escaping changed.");
    }
}
