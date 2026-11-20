using SharpAstro.Webp;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <c>Vp8LPrefixCode.HeaderBits</c> counts the header without writing it, and the encoder ranks every candidate by that
/// count, so it must be exactly what <c>WriteHeader</c> writes: for the simple form (one or two symbols below 256), the
/// normal form full and trimmed, and every alphabet the encoder uses.
/// </summary>
public sealed class WebpPrefixCodeHeaderBitsTests
{
    [Theory]
    [InlineData(19)]
    [InlineData(40)]
    [InlineData(256)]
    [InlineData(280)]
    [InlineData(280 + 16)]
    [InlineData(280 + 2048)]
    public void HeaderBits_IsWhatWriteHeaderWrites(int alphabet)
    {
        var random = new Random(alphabet);
        for (var trial = 0; trial < 400; trial++)
        {
            var histogram = new int[alphabet];
            // From empty and one or two symbols (the simple form, and two symbols past 255 that cannot use it) to dense,
            // with skewed counts so lengths spread and runs of zeros and of equal lengths both occur.
            var used = trial switch
            {
                0 => 0,
                < 40 => 1 + trial % 2,
                _ => random.Next(1, alphabet + 1),
            };
            for (var s = 0; s < used; s++)
                histogram[random.Next(alphabet)] += 1 + (random.Next(4) == 0 ? random.Next(100000) : random.Next(8));
            if (trial % 7 == 0 && alphabet > 256) histogram[alphabet - 1] += 3; // a last symbol past 255

            var code = Vp8LPrefixCode.Build(histogram);
            var writer = new Vp8LBitWriter(64);
            code.WriteHeader(writer);
            code.HeaderBits().ShouldBe(writer.BitCount, $"alphabet {alphabet}, trial {trial}, {code.UsedSymbols} symbols");
            // Counting first and writing after must agree too (the plan is kept between them).
            var again = new Vp8LBitWriter(64);
            code.WriteHeader(again);
            again.BitCount.ShouldBe(writer.BitCount);
        }
    }
}
