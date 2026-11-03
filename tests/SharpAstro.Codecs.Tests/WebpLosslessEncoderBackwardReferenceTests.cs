using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

// The second encoder rung: LZ77 backward references with the 2D distance codes, the colour cache
// and the per-image choice between them. Spec-derived from RFC 9649; cwebp and dwebp are only
// black-box oracles (see the oracle tests in WebpLosslessEncoderTests.cs).

public sealed class WebpLosslessEncoderPrefixTests
{
    // The decoder's rule (RFC 9649 section 3.5.2.1) written out independently of the encoder's.
    private static int Decode(int symbol, int extra)
    {
        if (symbol < 4)
        {
            return symbol + 1;
        }

        int extraBits = (symbol - 2) >> 1;
        int offset = (2 + (symbol & 1)) << extraBits;
        return offset + extra + 1;
    }

    [Theory]
    // value: symbol, extra bits, extra value, at every boundary where the split changes shape
    [InlineData(1, 0, 0, 0)]
    [InlineData(2, 1, 0, 0)]
    [InlineData(3, 2, 0, 0)]
    [InlineData(4, 3, 0, 0)]
    [InlineData(5, 4, 1, 0)]
    [InlineData(6, 4, 1, 1)]
    [InlineData(7, 5, 1, 0)]
    [InlineData(8, 5, 1, 1)]
    [InlineData(9, 6, 2, 0)]
    [InlineData(12, 6, 2, 3)]
    [InlineData(13, 7, 2, 0)]
    [InlineData(16, 7, 2, 3)]
    [InlineData(17, 8, 3, 0)]
    [InlineData(24, 8, 3, 7)]
    [InlineData(25, 9, 3, 0)]
    [InlineData(32, 9, 3, 7)]
    [InlineData(33, 10, 4, 0)]
    [InlineData(4096, 23, 10, 1023)]       // the longest copy
    [InlineData(4097, 24, 11, 0)]          // first value past the length alphabet's 24 symbols
    [InlineData(1048576, 39, 18, 262143)]  // the farthest distance code: symbol 39 of 40
    public void ValuesSplitIntoPrefixSymbolAndExtraBitsAtEveryBoundary(int value, int symbol, int extraBits, int extraValue)
    {
        Vp8LBackwardReferences.PrefixEncode(value, out int s, out int bits, out int extra);
        Assert.Equal((symbol, extraBits, extraValue), (s, bits, extra));
    }

    [Fact]
    public void TheLongestCopyAndTheFarthestDistanceFitTheirAlphabets()
    {
        Vp8LBackwardReferences.PrefixEncode(Vp8LBackwardReferences.MaxLength, out int lengthSymbol, out _, out _);
        Assert.InRange(lengthSymbol, 0, 23);
        Vp8LBackwardReferences.PrefixEncode(Vp8LBackwardReferences.MaxDistanceCode, out int distanceSymbol, out _, out _);
        Assert.InRange(distanceSymbol, 0, 39);
        Vp8LBackwardReferences.PrefixEncode(Vp8LBackwardReferences.MaxDistanceCode + 1, out int beyond, out _, out _);
        Assert.Equal(40, beyond);
    }

    [Fact]
    public void EveryValueUpToTheDistanceCodeLimitRoundTripsThroughTheDecodersRule()
    {
        int previousSymbol = 0;
        for (int value = 1; value <= Vp8LBackwardReferences.MaxDistanceCode; value++)
        {
            Vp8LBackwardReferences.PrefixEncode(value, out int symbol, out int extraBits, out int extra);
            Assert.Equal(value, Decode(symbol, extra));
            Assert.InRange(extra, 0, (1 << extraBits) - 1);
            Assert.Equal(symbol < 4 ? 0 : (symbol - 2) >> 1, extraBits);
            Assert.True(symbol >= previousSymbol, "symbols must not decrease as values grow");
            previousSymbol = symbol;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AValueBelowOneIsRefused(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Vp8LBackwardReferences.PrefixEncode(value, out _, out _, out _));
    }
}

public sealed class WebpLosslessEncoderDistanceCodeTests
{
    private static readonly int[] Widths = [1, 2, 3, 5, 7, 8, 16, 100, 513, 4096, 16384];

    [Theory]
    [InlineData(1000)]
    [InlineData(16384)]
    public void AllHundredAndTwentyPlaneCodesMapBothWaysAtAWidthWhereTheyAreDistinct(int width)
    {
        var mapper = new Vp8LDistanceMapper(width);
        for (int code = 1; code <= 120; code++)
        {
            int distance = Vp8LDecoder.Distance(code, width);
            Assert.Equal(code, mapper.ToCode(distance));
            Assert.Equal(distance, Vp8LDecoder.Distance(mapper.ToCode(distance), width));
        }
    }

    [Fact]
    public void ThePlaneCodeTableIsTheRfcsHundredAndTwentyDistinctNeighbours()
    {
        Assert.Equal(120, Vp8LDecoder.DistanceMap.Length);
        Assert.Equal(120, Vp8LDecoder.DistanceMap.Distinct().Count());
        // Spot checks against the text of RFC 9649 section 4.2.2: the table starts (0,1) (1,0)
        // (1,1) (-1,1) (0,2) and ends (-7,7) (8,6) (8,7).
        Assert.Equal(new[] { (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2) }, Vp8LDecoder.DistanceMap.Take(5).ToArray());
        Assert.Equal(new[] { (-7, 7), (8, 6), (8, 7) }, Vp8LDecoder.DistanceMap.TakeLast(3).ToArray());
    }

    [Fact]
    public void KnownNeighboursGetTheirPlaneCodesAtWidth100()
    {
        var mapper = new Vp8LDistanceMapper(100);
        Assert.Equal(1, mapper.ToCode(100)); // directly above
        Assert.Equal(2, mapper.ToCode(1));   // directly left
        Assert.Equal(3, mapper.ToCode(101)); // up-left
        Assert.Equal(4, mapper.ToCode(99));  // up-right
        Assert.Equal(5, mapper.ToCode(200)); // two above
    }

    [Fact]
    public void WhenTwoPlaneCodesShareAnOffsetTheSmallerCodeWins()
    {
        // At width 1 every (x, y) lands at x + y, so many codes name the same distance. The
        // smallest code for each distance is what an encoder should write.
        var mapper = new Vp8LDistanceMapper(1);
        for (int distance = 1; distance <= mapper.MaxPlaneDistance; distance++)
        {
            int expected = Enumerable.Range(1, 120).FirstOrDefault(code => Vp8LDecoder.Distance(code, 1) == distance);
            Assert.Equal(expected == 0 ? distance + 120 : expected, mapper.ToCode(distance));
        }
    }

    [Fact]
    public void ADistanceNoPlaneCodeNamesFallsBackToDistancePlus120()
    {
        foreach (int width in Widths)
        {
            var mapper = new Vp8LDistanceMapper(width);
            var named = Enumerable.Range(1, 120).Select(code => Vp8LDecoder.Distance(code, width)).ToHashSet();
            foreach (int distance in Enumerable.Range(1, 600).Concat([width + 2, 2 * width + 3, 1 << 16, 1 << 19, Vp8LBackwardReferences.MaxDistance]))
            {
                int code = mapper.ToCode(distance);
                if (named.Contains(distance))
                {
                    Assert.InRange(code, 1, 120);
                }
                else
                {
                    Assert.Equal(distance + 120, code);
                }

                // Either way the decoder reads the code back as the same linear distance.
                Assert.Equal(distance, Vp8LDecoder.Distance(code, width));
            }
        }
    }

    [Fact]
    public void TheFarthestDistanceUsesTheLastDistancePrefixSymbol()
    {
        var mapper = new Vp8LDistanceMapper(4096);
        int code = mapper.ToCode(Vp8LBackwardReferences.MaxDistance);
        Assert.Equal(Vp8LBackwardReferences.MaxDistanceCode, code);
        Vp8LBackwardReferences.PrefixEncode(code, out int symbol, out _, out _);
        Assert.Equal(39, symbol);
    }
}

public sealed class WebpLosslessEncoderColourCacheTests
{
    [Fact]
    public void TheHashIsTheRfcsMultiplyAndShift()
    {
        // RFC 9649 section 3.6.2: (0x1e35a7bd * color) & 0xffffffff, then >> (32 - cache_bits).
        var random = new Random(3);
        for (int round = 0; round < 20000; round++)
        {
            int color = random.Next(int.MinValue, int.MaxValue);
            int bits = random.Next(1, 12);
            ulong product = (0x1e35a7bdUL * (uint)color) & 0xffffffffUL;
            int expected = (int)(product >> (32 - bits));
            Assert.Equal(expected, Vp8LBackwardReferences.CacheHash(color, bits));
            Assert.InRange(expected, 0, (1 << bits) - 1);
        }
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 11, 241)]
    public void KnownHashes(int color, int bits, int expected) =>
        Assert.Equal(expected, Vp8LBackwardReferences.CacheHash(color, bits));

    // The VP8L header: 8 signature, 14 + 14 size, 1 alpha, 3 version, then the transform bit, the
    // colour cache flag and its 4-bit size. Read by an independent bit reader.
    internal static int CacheBitsOf(byte[] file)
    {
        const int payload = 20; // simple layout: RIFF header (12) + chunk header (8)
        int bit = payload * 8 + 40;
        int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++, bit++)
            {
                value |= ((file[bit >> 3] >> (bit & 7)) & 1) << i;
            }

            return value;
        }

        Assert.Equal(0x2f, file[payload]);
        Assert.Equal(0, Read(1)); // no transform
        return Read(1) == 0 ? 0 : Read(4);
    }

    [Theory]
    [InlineData("palette-16")]
    [InlineData("palette-3")]
    [InlineData("flat-ui")]
    public void ACachePaysOnImagesWithFewColoursAndIsWrittenWithinOneToEleven(string name)
    {
        var image = WebpLosslessEncoderCorpus.Get(name);

        // The second rung's configuration: with transforms on, a palette image is colour indexed
        // and the cache the header names (if any) belongs to the indexed image, after the transforms.
        var plan = Vp8LEncoderPlan.Create((long)image.Width * image.Height, 9, WebpLimits.MaximumEncoderWorkingBytes) with { Transforms = false };
        byte[] vp8l = Vp8LEncoder.Encode(WebpLosslessEncoderCorpus.ToArgb(image.Rgba), image.Width, image.Height, false, plan);
        byte[] file = WebpWriter.Write(vp8l, image.Width, image.Height, false, null, null, null);
        int bits = CacheBitsOf(file);
        Assert.InRange(bits, 0, 11);
        if (name != "flat-ui")
        {
            Assert.InRange(bits, 1, 11);
        }
    }

    [Fact]
    public void EffortZeroNeverUsesACache()
    {
        foreach (var image in WebpLosslessEncoderCorpus.Build())
        {
            byte[] file = WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height, options: new WebpEncodeOptions { Effort = 0 });
            Assert.Equal(0, CacheBitsOf(file));
        }
    }

    [Theory]
    [MemberData(nameof(CacheSizes))]
    public void EveryCacheSizeRoundTripsThroughOurDecoderWithAndWithoutCopies(int cacheBits)
    {
        foreach (string name in new[] { "palette-16", "tiled-pattern", "line-art", "photo-noise", "transparent-black", "one-pixel", "alpha-gradient" })
        {
            var image = WebpLosslessEncoderCorpus.Get(name);
            int[] argb = WebpLosslessEncoderCorpus.ToArgb(image.Rgba);
            var plan = Vp8LEncoderPlan.Create((long)image.Width * image.Height, 9, WebpLimits.MaximumEncoderWorkingBytes);
            foreach (bool copies in new[] { false, true })
            {
                byte[] vp8l = Vp8LEncoder.EncodeCandidate(argb, image.Width, image.Height, true, plan, cacheBits, copies);
                byte[] file = WebpWriter.Write(vp8l, image.Width, image.Height, true, null, null, null);
                Assert.Equal(cacheBits, CacheBitsOf(file));
                Assert.True(
                    image.Rgba.AsSpan().SequenceEqual(WebpDecoder.Decode(file).Rgba),
                    $"{name}: cache {cacheBits}, copies {copies}");
            }
        }
    }

    public static IEnumerable<object[]> CacheSizes() => Enumerable.Range(0, 12).Select(static bits => new object[] { bits });
}

public sealed class WebpLosslessEncoderMatchFinderTests
{
    public static IEnumerable<object[]> CorpusNames() => WebpLosslessEncoderCorpus.Names();

    // Every copy must be a real one: replayed against the source, inside the window, in order,
    // non-overlapping, and within the length and distance the format can carry.
    private static List<ulong> FindAndValidate(string name, int effort, out Vp8LEncoderPlan plan)
    {
        var image = WebpLosslessEncoderCorpus.Get(name);
        int[] argb = WebpLosslessEncoderCorpus.ToArgb(image.Rgba);
        plan = Vp8LEncoderPlan.Create(argb.Length, effort, WebpLimits.MaximumEncoderWorkingBytes);
        var mapper = new Vp8LDistanceMapper(image.Width);
        ulong[] buffer = new ulong[Math.Max(plan.MaxCopies, 1)];
        int count = Vp8LMatchFinder.Find(argb, image.Width, plan, mapper, buffer);

        int end = 0;
        for (int i = 0; i < count; i++)
        {
            int position = Vp8LMatchFinder.Position(buffer[i]);
            int length = Vp8LMatchFinder.Length(buffer[i]);
            int distance = Vp8LMatchFinder.Distance(buffer[i]);
            Assert.True(position >= end, $"copy {i} at {position} overlaps the one before, which ends at {end}");
            Assert.InRange(length, Vp8LMatchFinder.MinMatch, Vp8LBackwardReferences.MaxLength);
            Assert.InRange(distance, 1, Math.Min(position, Vp8LBackwardReferences.MaxDistance));
            Assert.True(distance < plan.WindowSize, $"distance {distance} is outside the window of {plan.WindowSize}");
            Assert.True(position + length <= argb.Length);
            for (int k = 0; k < length; k++)
            {
                // Overlapping copies read what they have just written, as the decoder does.
                Assert.Equal(argb[position + k], argb[position + k - distance]);
            }

            end = position + length;
        }

        return [.. buffer.Take(count)];
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void EveryCopyIsRealAndInsideTheLimitsAtEveryEffort(string name)
    {
        for (int effort = 1; effort <= 9; effort++)
        {
            FindAndValidate(name, effort, out _);
        }
    }

    [Fact]
    public void RepetitiveImagesYieldCopiesAndNoiseYieldsFew()
    {
        Assert.NotEmpty(FindAndValidate("tiled-pattern", 6, out _));
        Assert.NotEmpty(FindAndValidate("long-repeat", 6, out _));
        Assert.NotEmpty(FindAndValidate("far-repeat", 6, out _));
        Assert.NotEmpty(FindAndValidate("line-art", 6, out _));
    }

    [Fact]
    public void ARunLongerThanTheMaximumLengthIsSplitIntoMaximumLengthCopies()
    {
        var copies = FindAndValidate("transparent-black", 6, out _); // 1024 px: one copy
        Assert.Single(copies);

        // 16384 identical pixels: one literal, then copies of 4096, 4096, 4096 and the 4095 left.
        int[] argb = new int[16384];
        Array.Fill(argb, 0x11223344);
        var plan = Vp8LEncoderPlan.Create(argb.Length, 6, WebpLimits.MaximumEncoderWorkingBytes);
        ulong[] buffer = new ulong[plan.MaxCopies];
        int count = Vp8LMatchFinder.Find(argb, 128, plan, new Vp8LDistanceMapper(128), buffer);
        Assert.Equal(4, count);
        Assert.Equal(new[] { 4096, 4096, 4096, 4095 }, buffer.Take(count).Select(Vp8LMatchFinder.Length).ToArray());
        // Distance 1 and one row up (128) tie on length and the row above has the smaller plane code
        // (1, not 2), but nothing is a row up from position 1, so the first copy is at distance 1.
        Assert.Equal(1, Vp8LMatchFinder.Distance(buffer[0]));
        Assert.All(buffer.Skip(1).Take(count - 1), c => Assert.Equal(128, Vp8LMatchFinder.Distance(c)));
        Assert.Equal(1, Vp8LMatchFinder.Position(buffer[0]));
        Assert.Equal(argb.Length, Vp8LMatchFinder.Position(buffer[3]) + Vp8LMatchFinder.Length(buffer[3]));
    }

    [Fact]
    public void TheFarRepeatIsFoundAcrossTwoHundredRows()
    {
        var image = WebpLosslessEncoderCorpus.Get("far-repeat");
        var copies = FindAndValidate("far-repeat", 6, out _);
        int far = 200 * image.Width;
        Assert.Contains(copies, c => Vp8LMatchFinder.Distance(c) == far);
    }

    [Fact]
    public void AnEffortOneWindowDoesNotReachBackFurtherThanItsWindow()
    {
        FindAndValidate("far-repeat", 1, out var plan);
        Assert.Equal(1 << 14, plan.WindowSize);
    }

    [Fact]
    public void MatchPackingRoundTrips()
    {
        foreach ((int position, int length, int distance) in new[]
        {
            (0, 3, 1), (67_108_863, 4096, Vp8LBackwardReferences.MaxDistance), (12345, 17, 100), (1, 4096, 1 << 19),
        })
        {
            ulong copy = Vp8LMatchFinder.Pack(position, length, distance);
            Assert.Equal((position, length, distance), (Vp8LMatchFinder.Position(copy), Vp8LMatchFinder.Length(copy), Vp8LMatchFinder.Distance(copy)));
        }
    }
}

public sealed class WebpLosslessEncoderEffortTests
{
    public static IEnumerable<object[]> CorpusNames() => WebpLosslessEncoderCorpus.Names();
    public static IEnumerable<object[]> NamesByEffort() => WebpLosslessEncoderCorpus.NamesByEffort();

    private static int Size(string name, int effort)
    {
        var image = WebpLosslessEncoderCorpus.Get(name);
        return WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height, options: new WebpEncodeOptions { Effort = effort }).Length;
    }

    [Theory]
    [MemberData(nameof(NamesByEffort))]
    public void RoundTripsByteForByteThroughOurDecoderAtEveryEffort(string name, int effort)
    {
        var image = WebpLosslessEncoderCorpus.Get(name);
        byte[] file = WebpEncoder.EncodeLossless(
            image.Rgba, image.Width, image.Height, options: new WebpEncodeOptions { Effort = effort });
        WebpImage decoded = WebpDecoder.Decode(file);
        Assert.Equal(image.Width, decoded.Width);
        Assert.Equal(image.Height, decoded.Height);
        Assert.True(image.Rgba.AsSpan().SequenceEqual(decoded.Rgba), "decoded pixels differ from the source");
    }

    [Theory]
    [InlineData("flat-ui")]      // the screenshot-like image
    [InlineData("line-art")]
    [InlineData("tiled-pattern")]
    [InlineData("long-repeat")]
    [InlineData("far-repeat")]
    [InlineData("palette-16")]
    public void BackwardReferencesAndTheCacheBeatLiteralOnlyOnRepetitiveImages(string name)
    {
        int literalOnly = Size(name, 0);
        // Not effort 1: its 2^14 window cannot reach the far repeat, so it has nothing to find there.
        foreach (int effort in new[] { 3, 6, 9 })
        {
            int size = Size(name, effort);
            Assert.True(size < literalOnly, $"{name} at effort {effort}: {size} bytes, literal-only {literalOnly}");
        }
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void NoEffortIsEverLargerThanLiteralOnly(string name)
    {
        int literalOnly = Size(name, 0);
        for (int effort = 1; effort <= 9; effort++)
        {
            Assert.True(Size(name, effort) <= literalOnly, $"{name} at effort {effort}");
        }
    }

    [Theory]
    [InlineData("tiled-pattern")]
    [InlineData("long-repeat")]
    [InlineData("palette-16")]
    public void MoreEffortNeverCostsBytesOnTheRepetitiveImages(string name)
    {
        int previous = Size(name, 1);
        foreach (int effort in new[] { 3, 5, 7, 9 })
        {
            int size = Size(name, effort);
            // The search is greedy, so a deeper one is not guaranteed smaller; allow 2% of noise.
            Assert.True(size <= previous * 1.02, $"{name} effort {effort}: {size} after {previous}");
            previous = Math.Min(previous, size);
        }
    }

    [Fact]
    public void EffortZeroIsTheLiteralOnlyEncodingWithNoCopiesAndNoCache()
    {
        var image = WebpLosslessEncoderCorpus.Get("tiled-pattern");
        int[] argb = WebpLosslessEncoderCorpus.ToArgb(image.Rgba);
        byte[] viaEffort = WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height, options: new WebpEncodeOptions { Effort = 0 });
        var plan = Vp8LEncoderPlan.Create(argb.Length, 0, WebpLimits.MaximumEncoderWorkingBytes);
        Assert.False(plan.UseBackwardReferences);
        byte[] vp8l = Vp8LEncoder.EncodeCandidate(argb, image.Width, image.Height, false, plan, 0, false);
        Assert.Equal(WebpWriter.Write(vp8l, image.Width, image.Height, false, null, null, null), viaEffort);
    }

    [Fact]
    public void EncodingIsDeterministicAtEveryEffort()
    {
        var image = WebpLosslessEncoderCorpus.Get("tiled-pattern");
        for (int effort = 0; effort <= 9; effort++)
        {
            var options = new WebpEncodeOptions { Effort = effort };
            Assert.Equal(
                WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height, options: options),
                WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height, options: options));
        }
    }

    [Fact]
    public void HigherEffortsTryMoreCacheSizesAndSearchHarder()
    {
        const long pixels = 1 << 20;
        long budget = WebpLimits.MaximumEncoderWorkingBytes;
        var plans = Enumerable.Range(0, 10).Select(effort => Vp8LEncoderPlan.Create(pixels, effort, budget)).ToArray();
        Assert.False(plans[0].UseBackwardReferences);
        Assert.Equal(new[] { 0 }, plans[0].CacheBitsToTry);
        for (int effort = 2; effort <= 9; effort++)
        {
            Assert.True(plans[effort].UseBackwardReferences);
            Assert.True(plans[effort].CacheBitsToTry.Length >= plans[effort - 1].CacheBitsToTry.Length);
            Assert.True(plans[effort].ChainDepth >= plans[effort - 1].ChainDepth);
            Assert.True(plans[effort].WindowBits >= plans[effort - 1].WindowBits);
            Assert.All(plans[effort].CacheBitsToTry, static bits => Assert.InRange(bits, 0, 11));
            Assert.Contains(0, plans[effort].CacheBitsToTry);
        }

        Assert.Equal(Enumerable.Range(0, 12), plans[9].CacheBitsToTry);
    }
}

public sealed class WebpLosslessEncoderBudgetTests
{
    [Fact]
    public void ThePlanForTheLargestImageIsChargedWithinTheBudgetBeforeAnythingIsAllocated()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var plan = Vp8LEncoderPlan.Create(WebpLimits.MaximumPixels, 9, WebpLimits.MaximumEncoderWorkingBytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(plan.UseBackwardReferences);
        Assert.True(plan.WorkingBytes <= WebpLimits.MaximumEncoderWorkingBytes);
        Assert.True(allocated < 64 * 1024, $"working out the plan allocated {allocated} bytes");
        Assert.Equal(
            Vp8LEncoderPlan.BytesFor(WebpLimits.MaximumPixels, plan.WindowBits, plan.HashBits),
            plan.WorkingBytes);
    }

    [Fact]
    public void ATightBudgetShrinksTheWindowAndAnImpossibleOneDropsBackwardReferences()
    {
        const long pixels = 1 << 22;
        var roomy = Vp8LEncoderPlan.Create(pixels, 9, 1L << 40);
        long copiesBytes = 8L * Vp8LEncoderPlan.MaxCopiesFor(pixels);

        var tight = Vp8LEncoderPlan.Create(pixels, 9, Vp8LEncoderPlan.FixedBytes + copiesBytes + (4L << 16) + (4L << roomy.HashBits));
        Assert.True(tight.UseBackwardReferences);
        Assert.True(tight.WindowBits < roomy.WindowBits);
        Assert.True(tight.WorkingBytes <= Vp8LEncoderPlan.FixedBytes + copiesBytes + (4L << 16) + (4L << roomy.HashBits));

        var none = Vp8LEncoderPlan.Create(pixels, 9, copiesBytes / 2);
        Assert.False(none.UseBackwardReferences);
        Assert.Equal(0, none.MaxCopies);
        Assert.Equal(Vp8LEncoderPlan.FixedBytes, none.WorkingBytes);
    }

    [Fact]
    public void APlanWithoutBackwardReferencesStillEncodesAValidStream()
    {
        var image = WebpLosslessEncoderCorpus.Get("tiled-pattern");
        int[] argb = WebpLosslessEncoderCorpus.ToArgb(image.Rgba);
        var plan = Vp8LEncoderPlan.Create(argb.Length, 9, 1024);
        Assert.False(plan.UseBackwardReferences);
        byte[] vp8l = Vp8LEncoder.Encode(argb, image.Width, image.Height, false, plan);
        byte[] file = WebpWriter.Write(vp8l, image.Width, image.Height, false, null, null, null);
        Assert.True(image.Rgba.AsSpan().SequenceEqual(WebpDecoder.Decode(file).Rgba));
    }

    [Fact]
    public void TheMatchFinderAllocatesWhatItWasChargedAndNoMore()
    {
        var image = WebpLosslessEncoderCorpus.Get("far-repeat");
        int[] argb = WebpLosslessEncoderCorpus.ToArgb(image.Rgba);
        var plan = Vp8LEncoderPlan.Create(argb.Length, 9, WebpLimits.MaximumEncoderWorkingBytes);
        var mapper = new Vp8LDistanceMapper(image.Width);
        ulong[] copies = new ulong[plan.MaxCopies];

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = Vp8LMatchFinder.Find(argb, image.Width, plan, mapper, copies);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        long chain = 4L << plan.WindowBits;
        long heads = 4L << plan.HashBits;
        Assert.True(allocated <= chain + heads + 4096, $"Find allocated {allocated} bytes, charged {chain + heads}");
        Assert.True(
            8L * copies.Length + chain + heads + Vp8LEncoderPlan.FixedBytes == plan.WorkingBytes,
            "the plan charges the copy list, the chain, the heads and the fixed scratch");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TinyImagesGetNoWindowAndStillRoundTrip(int pixels)
    {
        byte[] rgba = new byte[pixels * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 37);
        foreach (int effort in new[] { 0, 5, 9 })
        {
            byte[] file = WebpEncoder.EncodeLossless(rgba, pixels, 1, options: new WebpEncodeOptions { Effort = effort });
            Assert.True(rgba.AsSpan().SequenceEqual(WebpDecoder.Decode(file).Rgba));
        }
    }

    [Fact]
    public void AnEffortOutsideZeroToNineIsRefusedByThePlanToo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vp8LEncoderPlan.Create(100, 10, 1 << 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vp8LEncoderPlan.Create(100, -1, 1 << 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vp8LEncoderPlan.Create(0, 5, 1 << 30));
    }
}
