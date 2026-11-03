using System.Diagnostics;
using SharpAstro.Webp;
using Xunit;

namespace SharpAstro.Codecs.Tests;

// What the lossless encoder costs on inputs built to be expensive, not typical. Three shapes:
//   * the largest dimensions, which must be refused before anything is allocated;
//   * every pixel a different colour, so there is no palette and almost no repeat to find;
//   * one colour, or a very short period, so every position matches every other and the hash
//     chains are as long as they can get.
//
// The bound on time is the chain-depth cap and the nice length in Vp8LEncoderPlan, per effort
// (ChainDepthByEffort 0, 2, 4 ... 512; NiceLengthByEffort 16 ... 4096): the match finder visits at
// most ChainDepth candidates for a position and stops extending a match at NiceLength, so the work
// is at most pixels x ChainDepth probes plus pixels x NiceLength compares for a candidate, however
// the pixels repeat. The bound on memory is on the bytes a whole encode ALLOCATES (cumulative, so
// well above what is live at once: the live part is the plan's charge, Vp8LEncoderPlan.WorkingBytes,
// capped at WebpLimits.MaximumEncoderWorkingBytes, plus the transformed copies of the image). The
// timeouts are generous (several times what was measured on the slowest machine here, an arm64 VM,
// with the rest of the suite running beside them), so they catch a quadratic blow-up rather than a
// slow machine.

public sealed class WebpLosslessEncoderPathologicalInputTests
{
    // Allocated bytes per pixel over a whole encode of a 1024 x 1024 image (measured on the
    // all-distinct image, the worst of the three shapes: effort 0 14.6, effort 6 859, effort 9
    // 1,375; the period images measured less), rounded up by half again. The bound is a multiple of
    // the pixel count, so it also states that the memory grows linearly with the image and no
    // faster. Effort 9 keeps eight finalists and sweeps every predictor tile size, which is where
    // the kilobyte a pixel goes. A photo costs more than these images (the 1080p benchmark's
    // effort 9 allocates 5.5 GB for 2 M pixels of photo, 2.7 KB a pixel), so these figures are
    // this test's bounds on these shapes, not a ceiling for every input.
    private const long BytesPerPixelEffort0 = 32;
    private const long BytesPerPixelEffort6 = 1300;
    private const long BytesPerPixelEffort9 = 2100;

    private static long BytesPerPixelFor(int effort) => effort switch
    {
        0 => BytesPerPixelEffort0,
        9 => BytesPerPixelEffort9,
        _ => BytesPerPixelEffort6,
    };

    /// <summary>1024 x 1024 pixels, every one a different opaque colour (an odd step modulo 2^24 visits each colour once).</summary>
    private static byte[] AllDistinct(int width, int height)
    {
        byte[] rgba = new byte[checked(width * height * 4)];
        uint colour = 0;
        for (int i = 0; i < width * height; i++)
        {
            colour = (colour + 0x9E3779B1u) & 0xFFFFFF; // odd, so a permutation of the 2^24 colours
            rgba[4 * i] = (byte)(colour >> 16);
            rgba[4 * i + 1] = (byte)(colour >> 8);
            rgba[4 * i + 2] = (byte)colour;
            rgba[4 * i + 3] = 255;
        }

        return rgba;
    }

    private static byte[] Period(int width, int height, int period)
    {
        byte[] rgba = new byte[checked(width * height * 4)];
        for (int i = 0; i < width * height; i++)
        {
            int k = i % period;
            rgba[4 * i] = (byte)(30 + 60 * k);
            rgba[4 * i + 1] = (byte)(200 - 50 * k);
            rgba[4 * i + 2] = (byte)(7 * k);
            rgba[4 * i + 3] = 255;
        }

        return rgba;
    }

    private static (byte[] File, long Allocated, TimeSpan Elapsed) Measure(byte[] rgba, int width, int height, int effort)
    {
        // The encoder is single-threaded, so the current thread's count is the encode's and is not
        // disturbed by the other tests running in parallel (GC.GetTotalAllocatedBytes would be).
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        byte[] file = WebpEncoder.EncodeLossless(rgba, width, height, 0, new WebpEncodeOptions { Effort = effort });
        clock.Stop();
        return (file, GC.GetAllocatedBytesForCurrentThread() - before, clock.Elapsed);
    }

    private static void AssertRoundTrips(byte[] rgba, int width, int height, byte[] file)
    {
        WebpImage decoded = WebpDecoder.Decode(file);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.True(rgba.AsSpan().SequenceEqual(decoded.Rgba), "the decoded pixels differ from the source");
    }

    // ---- the largest dimensions ---------------------------------------------------------------------

    [Theory]
    [InlineData(16385, 1)]
    [InlineData(1, 16385)]
    [InlineData(16384, 16384)]
    [InlineData(16384, 8192)]
    [InlineData(8192, 16384)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void DimensionsOverTheLimitsAreRefusedBeforeAnythingIsAllocated(int width, int height)
    {
        // The buffer is empty on purpose: the refusal has to come from the dimensions, so no raster
        // (4 x 2^28 bytes for 16384 x 16384) and no plan is ever made.
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (int effort in new[] { 0, 6, 9 })
        {
            var options = new WebpEncodeOptions { Effort = effort };
            Assert.Throws<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossless([], width, height, 0, options));
            Assert.Throws<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLosslessRgb([], width, height, 0, options));
            Assert.Throws<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLosslessGray([], width, height, 0, options));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64 * 1024, $"nine refusals allocated {allocated} bytes");
    }

    [Theory]
    [InlineData(16384, 4096)] // exactly 2^26 pixels, the decoder's limit
    [InlineData(4096, 16384)]
    public void TheLargestAllowedImageIsNotRefusedForSizeAndAShortBufferIsRefusedBeforeAnyWork(int width, int height)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(() => WebpEncoder.EncodeLossless(new byte[16], width, height));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64 * 1024, $"the refusal allocated {allocated} bytes");
    }

    // ---- the working memory the plan will charge for the largest image -----------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void TheLargestImagesPlanStaysInsideTheWorkingMemoryCeilingAtEveryEffort(int effort)
    {
        // Above LargeImagePixels one candidate is kept, whatever the effort asks for.
        var plan = Vp8LEncoderPlan.Create(WebpLimits.MaximumPixels, effort, WebpLimits.MaximumEncoderWorkingBytes);
        Assert.True(plan.WorkingBytes <= WebpLimits.MaximumEncoderWorkingBytes);
        Assert.True(plan.Finalists <= 1);
        Assert.True(plan.ChainDepth <= 512);
        Assert.True(plan.NiceLength <= 4096);
    }

    // ---- every pixel a different colour ----------------------------------------------------------

    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(9)]
    public void AnImageOfOneMillionDistinctColoursEncodesWithinTheBounds(int effort)
    {
        const int width = 1024;
        const int height = 1024;
        byte[] rgba = AllDistinct(width, height);
        var (file, allocated, elapsed) = Measure(rgba, width, height, effort);

        AssertRoundTrips(rgba, width, height, file);
        long bound = BytesPerPixelFor(effort) * width * height;
        Assert.True(allocated <= bound, $"effort {effort}: allocated {allocated} bytes, bound {bound}; took {elapsed.TotalMilliseconds:F0} ms");
        // Nothing here repeats, so there is nothing to find and the file is about the size of the
        // pixels (24 bits each, a little over for the codes): it never grows past 4 bytes a pixel.
        Assert.True(file.Length < 4L * width * height, $"effort {effort}: file is {file.Length} bytes");
    }

    // ---- hash-chain stress: one colour, and a short period ----------------------------------------

    [Theory(Timeout = 120_000)]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(9, 1)]
    [InlineData(6, 2)]
    [InlineData(9, 2)]
    [InlineData(9, 3)]
    [InlineData(9, 7)]
    public void AnImageWhoseEveryPositionMatchesEveryOtherEncodesWithinTheBounds(int effort, int period)
    {
        // With a period of 1 every position hashes to the same chain, so each position meets the
        // chain-depth cap (2 to 512 by effort) and each match meets the nice length.
        const int width = 1024;
        const int height = 1024;
        byte[] rgba = Period(width, height, period);
        var (file, allocated, elapsed) = Measure(rgba, width, height, effort);

        AssertRoundTrips(rgba, width, height, file);
        long bound = BytesPerPixelFor(effort) * width * height;
        Assert.True(allocated <= bound, $"effort {effort}, period {period}: allocated {allocated} bytes, bound {bound}; took {elapsed.TotalMilliseconds:F0} ms");
        // A repeat is one copy per 4096 pixels at most, so the file is tiny, not proportional to the image.
        Assert.True(file.Length < 64 * 1024, $"effort {effort}, period {period}: file is {file.Length} bytes");
    }

    [Fact(Timeout = 120_000)]
    public void OneColourWithASprinkleOfOthersEncodesWithinTheBounds()
    {
        // Long runs broken now and then: the chains stay long and the matches keep being cut short.
        const int width = 1024;
        const int height = 1024;
        byte[] rgba = Period(width, height, 1);
        for (int i = 977; i < width * height; i += 977)
        {
            rgba[4 * i] ^= 0x55;
            rgba[4 * i + 2] ^= (byte)i;
        }

        var (file, allocated, elapsed) = Measure(rgba, width, height, 9);

        AssertRoundTrips(rgba, width, height, file);
        long bound = BytesPerPixelEffort9 * width * height;
        Assert.True(allocated <= bound, $"allocated {allocated} bytes, bound {bound}; took {elapsed.TotalMilliseconds:F0} ms");
    }
}
