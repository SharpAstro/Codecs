using System.Numerics;

namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 sections 3.5.2 (LZ77 backward
// reference), 4.2.2 (distance mapping) and 3.6.2 (colour cache) only. No libwebp, libvpx or
// RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are black-box test oracles.

/// <summary>
/// The shared arithmetic of VP8L backward references: the prefix code with extra bits that lengths
/// and distance codes use, and the colour cache hash.
/// </summary>
internal static class Vp8LBackwardReferences
{
    /// <summary>RFC 9649 section 3.5.2: a copy is at most 4096 pixels long.</summary>
    public const int MaxLength = 4096;

    /// <summary>
    /// The largest distance code the 40 distance prefix symbols can carry (symbol 39 with 18 extra
    /// bits), and so, once 120 is added for the plane-code offset, the farthest linear distance.
    /// </summary>
    public const int MaxDistanceCode = 1 << 20;

    /// <summary>How many plane codes the 2D neighbourhood table has; larger codes are distance + 120.</summary>
    public const int PlaneCodeCount = 120;

    /// <summary>The farthest linear distance an encoder can write: <c>MaxDistanceCode - 120</c>.</summary>
    public const int MaxDistance = MaxDistanceCode - PlaneCodeCount;

    /// <summary>The smallest and largest colour cache sizes in bits (RFC 9649 section 3.6.2).</summary>
    public const int MinCacheBits = 1;

    /// <inheritdoc cref="MinCacheBits"/>
    public const int MaxCacheBits = 11;

    /// <summary>
    /// Splits a length or distance code <paramref name="value"/> (1 or more) into its prefix symbol
    /// and the extra bits that follow it (RFC 9649 section 3.5.2.1): values up to 4 are their own
    /// symbol (value - 1); above that the symbol names the top two bits of value - 1 and the rest
    /// are written raw.
    /// </summary>
    public static void PrefixEncode(int value, out int symbol, out int extraBits, out int extraValue)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A length or distance code is at least 1.");
        }

        if (value <= 4)
        {
            symbol = value - 1;
            extraBits = 0;
            extraValue = 0;
            return;
        }

        int d = value - 1;
        int highestBit = 31 - BitOperations.LeadingZeroCount((uint)d);
        int secondHighestBit = (d >> (highestBit - 1)) & 1;
        extraBits = highestBit - 1;
        extraValue = d & ((1 << extraBits) - 1);
        symbol = 2 * highestBit + secondHighestBit;
    }

    /// <summary>The colour cache slot of <paramref name="argb"/> for a cache of <paramref name="cacheBits"/> bits.</summary>
    public static int CacheHash(int argb, int cacheBits) =>
        (int)(unchecked(0x1e35a7bdu * (uint)argb) >> (32 - cacheBits));
}

/// <summary>
/// Maps a linear backward distance to the distance code to write at one image width: the smallest
/// of the 120 plane codes whose (xi, yi) neighbour lies at that distance, otherwise distance + 120.
/// The 120-entry table is the decoder's own (<see cref="Vp8LDecoder.DistanceMap"/>), so the two
/// cannot drift apart.
/// </summary>
internal sealed class Vp8LDistanceMapper
{
    private readonly int[] _distances; // the distinct linear distances of the table, ascending
    private readonly int[] _codes;     // the smallest plane code at each of them

    public Vp8LDistanceMapper(int width)
    {
        // RFC 9649 section 4.2.2: dist = xi + yi * xsize, and a result under 1 is 1.
        var pairs = new long[Vp8LBackwardReferences.PlaneCodeCount];
        for (int code = 1; code <= pairs.Length; code++)
        {
            pairs[code - 1] = ((long)Vp8LDecoder.Distance(code, width) << 8) | (uint)code;
        }

        Array.Sort(pairs);
        var distances = new List<int>(pairs.Length);
        var codes = new List<int>(pairs.Length);
        foreach (long pair in pairs)
        {
            int distance = (int)(pair >> 8);
            if (distances.Count == 0 || distances[^1] != distance)
            {
                distances.Add(distance);
                codes.Add((int)(pair & 0xff)); // sorted by code within a distance, so the first is smallest
            }
        }

        _distances = [.. distances];
        _codes = [.. codes];
    }

    /// <summary>The farthest distance any plane code stands for at this width.</summary>
    public int MaxPlaneDistance => _distances[^1];

    /// <summary>The distance code to write for <paramref name="distance"/> (1 to <see cref="Vp8LBackwardReferences.MaxDistance"/>).</summary>
    public int ToCode(int distance)
    {
        if (distance > _distances[^1])
        {
            return distance + Vp8LBackwardReferences.PlaneCodeCount;
        }

        int index = Array.BinarySearch(_distances, distance);
        return index >= 0 ? _codes[index] : distance + Vp8LBackwardReferences.PlaneCodeCount;
    }
}
