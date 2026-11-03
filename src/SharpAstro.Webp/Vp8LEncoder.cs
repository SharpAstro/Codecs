namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 only. No libwebp, libvpx or
// RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are used only as black-box
// test oracles.

/// <summary>
/// Which transforms a candidate encoding applies, in the order the stream carries them (palette,
/// subtract green, predictor, cross colour; each at most once). Everything here is a choice the
/// encoder makes by itself at <c>Encode</c>; the tests build one by hand to force it.
/// </summary>
internal sealed record Vp8LPipeline
{
    /// <summary>No transform at all.</summary>
    public static readonly Vp8LPipeline None = new();

    /// <summary>Colour indexing: needs at most 256 distinct colours.</summary>
    public bool Palette { get; init; }

    /// <summary>Order the palette by first appearance instead of by value.</summary>
    public bool FirstSeenPalette { get; init; }

    /// <summary>Subtract green from red and blue.</summary>
    public bool SubtractGreen { get; init; }

    /// <summary>The predictor transform's tile size as log2 (2 to 9), or 0 for none.</summary>
    public int PredictorBits { get; init; }

    /// <summary>A predictor mode (0 to 13) forced onto every tile, or -1 to choose per tile.</summary>
    public int ForcedPredictorMode { get; init; } = -1;

    /// <summary>The cross colour transform.</summary>
    public bool CrossColor { get; init; }

    /// <summary>The cross colour tile size as log2 (2 to 9); 0 means the predictor's, or 5 without one.</summary>
    public int CrossBits { get; init; }

    /// <summary>The multipliers forced onto every cross colour tile, or null to search.</summary>
    public (int GreenToRed, int GreenToBlue, int RedToBlue)? ForcedCross { get; init; }

    internal int EffectiveCrossBits => CrossBits != 0 ? CrossBits : PredictorBits != 0 ? PredictorBits : 5;
}

/// <summary>
/// The VP8L encoder (RFC 9649 sections 3.3 to 3.7 and 4) at its third rung: the four transforms
/// (subtract green, predictor, cross colour, colour indexing), LZ77 backward references with the 2D
/// distance codes, the colour cache, and meta prefix codes (an entropy image grouping tiles that
/// share statistics). Every choice is made by cost.
/// </summary>
/// <remarks>
/// Effort 0 is the literal-only encoder: no transform, no copies, no cache, one prefix-code group,
/// the same code with nothing to search and nothing to try. From effort 1 a set of candidate
/// pipelines (transform combinations, with their tile sizes) is prepared, each is ranked by the exact
/// cost of coding it literal-only, and the best few (more as the effort rises) get the full search
/// for copies and a cache size and, from effort 4, for a clustering of tiles into prefix-code
/// groups. The cheapest finished candidate is written, and from effort 3 the untransformed image is
/// always among them, so the output is never larger than the previous rung's. A candidate is costed
/// exactly (header bits plus symbol bits plus extra bits) before it is chosen.
/// </remarks>
internal static class Vp8LEncoder
{
    /// <summary>The VP8L signature byte (RFC 9649 section 3.2).</summary>
    public const byte Signature = 0x2f;

    // Green codes 256 literals, 24 length prefix codes and the colour cache indices; red, blue
    // and alpha code 256 literals; the distance code has 40 prefix symbols.
    private const int LiteralAlphabet = 256;
    private const int LengthSymbols = 24;

    // RFC 9649 section 4: the transform type numbers.
    private const int PredictorType = 0;
    private const int CrossColorType = 1;
    private const int SubtractGreenType = 2;
    private const int ColorIndexingType = 3;

    /// <summary>
    /// Encodes <paramref name="argb"/> (one 0xAARRGGBB int per pixel, row-major, tightly packed) as
    /// the payload of a <c>VP8L</c> chunk. <paramref name="width"/> and <paramref name="height"/>
    /// must be 1 to 16384.
    /// </summary>
    public static byte[] Encode(int[] argb, int width, int height, bool alphaIsUsed, int effort)
    {
        long pixelCount = (long)width * height;

        // The candidate pipelines' transformed copies of the image come out of the same budget
        // as the match finder's tables.
        int finalists = Vp8LEncoderPlan.Create(pixelCount, effort, WebpLimits.MaximumEncoderWorkingBytes).Finalists;
        long budget = Math.Max(Vp8LEncoderPlan.FixedBytes, WebpLimits.MaximumEncoderWorkingBytes - 4L * pixelCount * (finalists + 1));
        var plan = Vp8LEncoderPlan.Create(pixelCount, effort, budget);
        return Encode(argb, width, height, alphaIsUsed, plan);
    }

    /// <inheritdoc cref="Encode(int[], int, int, bool, int)"/>
    public static byte[] Encode(ReadOnlySpan<int> argb, int width, int height, bool alphaIsUsed, int effort) =>
        Encode(argb.ToArray(), width, height, alphaIsUsed, effort);

    /// <summary>Encodes with an explicit <paramref name="plan"/> (the tests make their own budgets).</summary>
    public static byte[] Encode(ReadOnlySpan<int> argb, int width, int height, bool alphaIsUsed, in Vp8LEncoderPlan plan)
    {
        long pixelCount = (long)width * height;
        if (argb.Length < pixelCount)
        {
            throw new ArgumentException("The pixel span is shorter than width * height.", nameof(argb));
        }

        return Encode(argb[..(int)pixelCount].ToArray(), width, height, alphaIsUsed, plan);
    }

    /// <summary>Encodes with an explicit <paramref name="plan"/>, choosing the cheapest candidate.</summary>
    public static byte[] Encode(int[] argb, int width, int height, bool alphaIsUsed, in Vp8LEncoderPlan plan)
    {
        long pixelCount = (long)width * height;
        if (argb.Length < pixelCount)
        {
            throw new ArgumentException("The pixel array is shorter than width * height.", nameof(argb));
        }

        if (argb.Length != pixelCount)
        {
            argb = argb[..(int)pixelCount];
        }

        // The untransformed image is always a candidate. With transforms on, the others are
        // prepared one at a time and only the best few by cheap cost are kept.
        Prepared untransformed = Prepare(argb, width, height, Vp8LPipeline.None, plan, null);
        var kept = new List<(Prepared Prepared, long Cheap)>();
        long untransformedCheap = untransformed.HeaderBits + CheapBits(untransformed);
        kept.Add((untransformed, untransformedCheap));

        if (plan.UseTransforms)
        {
            bool havePalette = Vp8LTransforms.TryCollectPalette(argb, out int[] palette);
            var memo = new PredictorMemo();
            foreach (Vp8LPipeline pipeline in Pipelines(plan, width, height, havePalette ? palette.Length : 0))
            {
                Prepared prepared = Prepare(argb, width, height, pipeline, plan, havePalette ? palette : null, memo);
                long cheap = prepared.HeaderBits + CheapBits(prepared);
                kept.Add((prepared, cheap));
                kept.Sort(static (a, b) => a.Cheap.CompareTo(b.Cheap));
                // The untransformed image is never dropped to make room (it costs no memory).
                while (kept.Count(static k => k.Prepared.Steps.Count > 0) > plan.Finalists)
                {
                    int drop = kept.FindLastIndex(static k => k.Prepared.Steps.Count > 0);
                    kept.RemoveAt(drop);
                }
            }
        }

        // Full search on the finalists. Without AlwaysTryUntransformed the untransformed image is
        // only kept if its cheap cost ranked it among them.
        bool keepUntransformed = plan.AlwaysTryUntransformed || !plan.UseTransforms
            || kept.FindIndex(static k => k.Prepared.Steps.Count == 0) < Math.Max(1, plan.Finalists);
        Prepared? bestPrepared = null;
        StreamPlan? bestStream = null;
        long bestBits = long.MaxValue;
        foreach ((Prepared prepared, _) in kept)
        {
            if (prepared.Steps.Count == 0 && !keepUntransformed)
            {
                continue;
            }

            StreamPlan stream = ChooseStream(prepared.Pixels, prepared.Width, height, plan, isMain: true);
            long bits = prepared.HeaderBits + stream.Bits;
            if (bits < bestBits)
            {
                bestBits = bits;
                bestPrepared = prepared;
                bestStream = stream;
            }
        }

        return Write(width, height, alphaIsUsed, bestPrepared!, bestStream!);
    }

    /// <summary>
    /// Encodes with exactly one candidate instead of choosing the cheapest: the copies the plan's
    /// match finder finds (or none), a cache of <paramref name="cacheBits"/> bits (0 for none), the
    /// transforms of <paramref name="pipeline"/> (none when null) and, when
    /// <paramref name="metaBits"/> is 2 to 9, an entropy image of tiles of that size with the tiles
    /// clustered into groups (even if one group would be cheaper). For tests, which need every
    /// choice written, not just the ones that pay.
    /// </summary>
    internal static byte[] EncodeCandidate(
        ReadOnlySpan<int> argb,
        int width,
        int height,
        bool alphaIsUsed,
        in Vp8LEncoderPlan plan,
        int cacheBits,
        bool useCopies,
        Vp8LPipeline? pipeline = null,
        int metaBits = 0)
    {
        int[] pixels = argb[..checked(width * height)].ToArray();
        Prepared prepared = Prepare(pixels, width, height, pipeline ?? Vp8LPipeline.None, plan, null);
        StreamPlan stream = BuildStream(
            prepared.Pixels,
            prepared.Width,
            height,
            plan,
            isMain: true,
            cacheBits,
            useCopies,
            metaBits);
        return Write(width, height, alphaIsUsed, prepared, stream);
    }

    // ---- pipelines --------------------------------------------------------------------------

    // The candidate pipelines for an image, other than "none", in the order they are tried.
    private static IEnumerable<Vp8LPipeline> Pipelines(Vp8LEncoderPlan plan, int width, int height, int paletteSize)
    {
        // A transform needs at least one pixel it can make a difference to; a 1x1 image has none.
        bool tiny = (long)width * height < 2;
        int[] bitsList = plan.PredictorBitsToTry;
        if (!tiny)
        {
            if (plan.TrySubtractGreen)
            {
                yield return new Vp8LPipeline { SubtractGreen = true };
            }

            foreach (int bits in bitsList)
            {
                yield return new Vp8LPipeline { SubtractGreen = true, PredictorBits = bits };
                if (plan.TryCrossColor)
                {
                    yield return new Vp8LPipeline { SubtractGreen = true, PredictorBits = bits, CrossColor = true };
                }

                if (plan.TryPredictorAlone)
                {
                    yield return new Vp8LPipeline { PredictorBits = bits };
                    if (plan.TryCrossColor)
                    {
                        yield return new Vp8LPipeline { PredictorBits = bits, CrossColor = true };
                    }
                }
            }
        }

        if (paletteSize > 0)
        {
            yield return new Vp8LPipeline { Palette = true };
            if (plan.TryFirstSeenPalette && paletteSize > 1)
            {
                yield return new Vp8LPipeline { Palette = true, FirstSeenPalette = true };
            }

            // A predictor over the indices pays for photo-like palettes, rarely for bundled ones.
            if (!tiny && bitsList.Length > 0 && (Vp8LTransforms.PaletteBits(paletteSize) == 0 || plan.TryPaletteThenPredictor))
            {
                yield return new Vp8LPipeline { Palette = true, PredictorBits = bitsList[0] };
                if (plan.TryFirstSeenPalette && paletteSize > 1)
                {
                    yield return new Vp8LPipeline { Palette = true, FirstSeenPalette = true, PredictorBits = bitsList[0] };
                }
            }
        }
    }

    // The predictor stage of the pipeline just prepared, kept so that the next one, which usually
    // differs only by a cross colour transform on top, does not search the modes again. A cross
    // colour pass clones the residuals first, so the memo's array is never written to.
    private sealed class PredictorMemo
    {
        public (bool Palette, bool FirstSeen, bool SubtractGreen, int Bits, int Forced)? Key;
        public int[]? Residuals;
        public TransformStep? Step;
    }

    private sealed record TransformStep(int Type, int Bits, int PaletteSize, StreamPlan? Sub, int[]? SubPixels, int SubWidth, int SubHeight);

    private sealed class Prepared
    {
        public required int[] Pixels;
        public required int Width;
        public required List<TransformStep> Steps;

        /// <summary>Bits of every transform (its marker, type, parameters and sub-image) and the end marker.</summary>
        public required long HeaderBits;
    }

    // Applies the pipeline's transforms forward, in stream order, and codes each one's sub-image.
    private static Prepared Prepare(
        int[] argb,
        int width,
        int height,
        Vp8LPipeline pipeline,
        in Vp8LEncoderPlan plan,
        int[]? paletteColors,
        PredictorMemo? memo = null)
    {
        var steps = new List<TransformStep>();
        int[] pixels = argb;
        int currentWidth = width;
        bool owned = false;

        if (pipeline.Palette)
        {
            if (paletteColors is null && !Vp8LTransforms.TryCollectPalette(argb, out paletteColors))
            {
                throw new ArgumentException("The image has more than 256 colours, so it cannot be colour indexed.");
            }

            int[] colors = paletteColors!;
            if (!pipeline.FirstSeenPalette)
            {
                colors = [.. colors];
                Array.Sort(colors, static (a, b) => ((uint)a).CompareTo((uint)b));
            }

            int bits = Vp8LTransforms.PaletteBits(colors.Length);
            pixels = Vp8LTransforms.IndexPixels(argb, width, height, colors, bits);
            currentWidth = Vp8LTransforms.TileCount(width, bits);
            owned = true;
            int[] deltas = Vp8LTransforms.DeltaCodePalette(colors);
            steps.Add(new TransformStep(
                ColorIndexingType,
                bits,
                colors.Length,
                ChooseStream(deltas, colors.Length, 1, plan, isMain: false),
                deltas,
                colors.Length,
                1));
        }

        if (pipeline.SubtractGreen)
        {
            if (!owned)
            {
                pixels = (int[])pixels.Clone();
                owned = true;
            }

            Vp8LTransforms.SubtractGreen(pixels);
            steps.Add(new TransformStep(SubtractGreenType, 0, 0, null, null, 0, 0));
        }

        if (pipeline.PredictorBits != 0)
        {
            int bits = pipeline.PredictorBits;
            var key = (pipeline.Palette, pipeline.FirstSeenPalette, pipeline.SubtractGreen, bits, pipeline.ForcedPredictorMode);
            if (memo is { Key: { } remembered } && remembered == key)
            {
                pixels = memo.Residuals!;
                steps.Add(memo.Step!);
            }
            else
            {
                int[] modes = Vp8LTransforms.ChoosePredictorModes(pixels, currentWidth, height, bits, pipeline.ForcedPredictorMode);
                int[] residuals = new int[pixels.Length];
                Vp8LTransforms.PredictorForward(pixels, currentWidth, height, bits, modes, residuals);
                pixels = residuals;
                int tilesWide = Vp8LTransforms.TileCount(currentWidth, bits);
                int tilesHigh = Vp8LTransforms.TileCount(height, bits);
                int[] modePixels = new int[modes.Length];
                for (int i = 0; i < modes.Length; i++)
                {
                    modePixels[i] = Vp8LTransforms.ModePixel(modes[i]);
                }

                var step = new TransformStep(
                    PredictorType,
                    bits,
                    0,
                    ChooseStream(modePixels, tilesWide, tilesHigh, plan, isMain: false),
                    modePixels,
                    tilesWide,
                    tilesHigh);
                steps.Add(step);
                if (memo is not null)
                {
                    memo.Key = key;
                    memo.Residuals = residuals;
                    memo.Step = step;
                }
            }

            // Whatever follows (cross colour) works in place, so it gets its own copy.
            owned = false;
        }

        if (pipeline.CrossColor)
        {
            if (!owned)
            {
                pixels = (int[])pixels.Clone();
                owned = true;
            }

            int bits = pipeline.EffectiveCrossBits;
            int[] tiles = Vp8LTransforms.ChooseCrossColor(pixels, currentWidth, height, bits, plan.CrossColorMinimumStep, pipeline.ForcedCross);
            Vp8LTransforms.CrossColorForward(pixels, currentWidth, height, bits, tiles);
            int tilesWide = Vp8LTransforms.TileCount(currentWidth, bits);
            int tilesHigh = Vp8LTransforms.TileCount(height, bits);
            steps.Add(new TransformStep(
                CrossColorType,
                bits,
                0,
                ChooseStream(tiles, tilesWide, tilesHigh, plan, isMain: false),
                tiles,
                tilesWide,
                tilesHigh));
        }

        long headerBits = 1; // the "no more transforms" marker
        foreach (TransformStep step in steps)
        {
            headerBits += 1 + 2; // marker, type
            headerBits += step.Type == ColorIndexingType ? 8 : step.Type is PredictorType or CrossColorType ? 3 : 0;
            headerBits += step.Sub?.Bits ?? 0;
        }

        return new Prepared { Pixels = pixels, Width = currentWidth, Steps = steps, HeaderBits = headerBits };
    }

    // ---- the entropy-coded image ------------------------------------------------------------

    /// <summary>One chosen way of coding an image: the copies, the cache, the groups and the prefix codes.</summary>
    private sealed class StreamPlan
    {
        public required bool IsMain;
        public required int CacheBits;
        public required ulong[] CopyBuffer;
        public required int CopyCount;
        public required Vp8LDistanceMapper Mapper;

        /// <summary>The entropy image's tile size as log2, or 0 when there is one prefix-code group.</summary>
        public required int GroupBits;
        public required int[]? TileGroups;
        public required int TilesWide;
        public required Vp8LPrefixCode[][] Groups;
        public required long CodeBits;
        public StreamPlan? EntropyImage;
        public int[]? EntropyPixels;
        public int EntropyWidth;
        public int EntropyHeight;

        /// <summary>The per-group histograms, kept only when a clustering needs them.</summary>
        public int[][]? Histograms;

        public ReadOnlySpan<ulong> Copies => CopyBuffer.AsSpan(0, CopyCount);

        public bool UseCopies => CopyCount > 0;

        /// <summary>Every bit of the image's stream: the cache and meta flags, the entropy image, the codes and the symbols.</summary>
        public long Bits =>
            1 + (CacheBits == 0 ? 0 : 4) + (IsMain ? 1 : 0)
            + (GroupBits == 0 ? 0 : 3 + EntropyImage!.Bits)
            + CodeBits;
    }

    // The exact cost of coding the image with one group, no copies and no cache: how the
    // candidate pipelines are ranked.
    private static long CheapBits(Prepared prepared)
    {
        var mapper = new Vp8LDistanceMapper(prepared.Width);
        StreamPlan stream = Evaluate(prepared.Pixels, prepared.Width, [], 0, mapper, 0, new int[1], 0, 0, null, 1, isMain: true);
        return stream.Bits;
    }

    // Searches copies and cache sizes (and, for the main image, a clustering into groups) and
    // returns the cheapest stream.
    private static StreamPlan ChooseStream(int[] argb, int width, int height, in Vp8LEncoderPlan parentPlan, bool isMain)
    {
        // A sub-image is its own small image: its own plan, sized from its own pixel count.
        Vp8LEncoderPlan plan = isMain
            ? parentPlan
            : Vp8LEncoderPlan.Create((long)width * height, parentPlan.Effort, WebpLimits.MaximumEncoderWorkingBytes);
        var mapper = new Vp8LDistanceMapper(width);

        // The one large allocation of the search, sized by the plan that was charged for it.
        ulong[] copyBuffer = plan.UseBackwardReferences ? new ulong[plan.MaxCopies] : [];
        int copyCount = Vp8LMatchFinder.Find(argb, width, plan, mapper, copyBuffer);
        int[] cache = new int[1 << Vp8LBackwardReferences.MaxCacheBits];

        // The literal-only encoding is always a candidate; with copies, every cache size the
        // effort asks for is another.
        StreamPlan best = Evaluate(argb, width, copyBuffer, 0, mapper, 0, cache, 0, 0, null, 1, isMain);
        if (copyCount > 0)
        {
            foreach (int cacheBits in plan.CacheBitsToTry)
            {
                StreamPlan candidate = Evaluate(argb, width, copyBuffer, copyCount, mapper, cacheBits, cache, 0, 0, null, 1, isMain);
                if (candidate.Bits < best.Bits)
                {
                    best = candidate;
                }
            }
        }
        else if (plan.Effort > 0)
        {
            // No copies to be had (noise, say), but a cache can still pay.
            foreach (int cacheBits in plan.CacheBitsToTry)
            {
                if (cacheBits == 0)
                {
                    continue;
                }

                StreamPlan candidate = Evaluate(argb, width, copyBuffer, 0, mapper, cacheBits, cache, 0, 0, null, 1, isMain);
                if (candidate.Bits < best.Bits)
                {
                    best = candidate;
                }
            }
        }

        if (isMain && plan.UseMetaPrefix)
        {
            foreach (int groupBits in plan.MetaBitsToTry(width, height))
            {
                StreamPlan? grouped = TryGroups(argb, width, height, plan, best, cache, groupBits, force: false);
                if (grouped is not null && grouped.Bits < best.Bits)
                {
                    best = grouped;
                }
            }
        }

        return best;
    }

    // The single candidate the test hook asks for.
    private static StreamPlan BuildStream(
        int[] argb,
        int width,
        int height,
        in Vp8LEncoderPlan plan,
        bool isMain,
        int cacheBits,
        bool useCopies,
        int metaBits)
    {
        var mapper = new Vp8LDistanceMapper(width);
        ulong[] copyBuffer = plan.UseBackwardReferences ? new ulong[plan.MaxCopies] : [];
        int copyCount = useCopies ? Vp8LMatchFinder.Find(argb, width, plan, mapper, copyBuffer) : 0;
        int[] cache = new int[1 << Vp8LBackwardReferences.MaxCacheBits];
        StreamPlan stream = Evaluate(argb, width, copyBuffer, copyCount, mapper, cacheBits, cache, 0, 0, null, 1, isMain);
        if (metaBits != 0)
        {
            stream = TryGroups(argb, width, height, plan, stream, cache, metaBits, force: true) ?? stream;
        }

        return stream;
    }

    // Clusters the tiles of tile size 1 << groupBits by the symbols of `baseline` (its copies and
    // cache) and prices the result exactly. Null when everything lands in one group (unless forced)
    // or the image is a single tile.
    private static StreamPlan? TryGroups(
        int[] argb,
        int width,
        int height,
        in Vp8LEncoderPlan plan,
        StreamPlan baseline,
        int[] cache,
        int groupBits,
        bool force)
    {
        int tilesWide = Vp8LTransforms.TileCount(width, groupBits);
        int tilesHigh = Vp8LTransforms.TileCount(height, groupBits);
        int tileCount = tilesWide * tilesHigh;
        if (tileCount < 2 && !force)
        {
            return null;
        }

        // One histogram set per tile, to cluster.
        int[] identity = new int[tileCount];
        for (int i = 0; i < identity.Length; i++)
        {
            identity[i] = i;
        }

        StreamPlan perTile = Evaluate(
            argb, width, baseline.CopyBuffer, baseline.CopyCount, baseline.Mapper, baseline.CacheBits, cache,
            groupBits, tilesWide, identity, tileCount, baseline.IsMain, keepHistograms: true);
        int[][] tileHistograms = perTile.Histograms!;
        var layout = new Vp8LHistogramLayout(LiteralAlphabet + LengthSymbols + (baseline.CacheBits == 0 ? 0 : 1 << baseline.CacheBits));
        int[] tileGroups = Vp8LHistogramClusterer.ClusterTiles(
            tileHistograms,
            layout,
            plan.UseMetaPrefix ? plan.MetaMaxGroups : 16,
            plan.UseMetaPrefix ? plan.MetaCandidatesPerCluster : 3,
            plan.MetaRefine,
            out int groupCount);
        if (groupCount < 2 && !force)
        {
            return null;
        }

        StreamPlan grouped = Evaluate(
            argb, width, baseline.CopyBuffer, baseline.CopyCount, baseline.Mapper, baseline.CacheBits, cache,
            groupBits, tilesWide, tileGroups, groupCount, baseline.IsMain);

        // The entropy image: each tile's group in red and green, coded as a sub-image of its own.
        int[] entropyPixels = new int[tileCount];
        for (int i = 0; i < entropyPixels.Length; i++)
        {
            entropyPixels[i] = Vp8LTransforms.Black | (tileGroups[i] << 8);
        }

        grouped.EntropyPixels = entropyPixels;
        grouped.EntropyWidth = tilesWide;
        grouped.EntropyHeight = tilesHigh;
        grouped.EntropyImage = ChooseStream(entropyPixels, tilesWide, tilesHigh, plan, isMain: false);
        return grouped;
    }

    private interface ISymbolSink
    {
        void Literal(int position, uint argb);

        void CacheHit(int position, int index);

        void Copy(int position, int length, int distanceCode);
    }

    // Replays the stream the way the decoder will read it: a pixel is a copy if one starts here,
    // else a colour cache hit if its slot holds it, else a literal. The cache is updated with every
    // pixel exactly as RFC 9649 section 3.6.2 has the decoder do, so the two stay in step.
    private static void Walk<TSink>(
        ReadOnlySpan<int> argb,
        ReadOnlySpan<ulong> copies,
        Vp8LDistanceMapper mapper,
        int cacheBits,
        int[] cache,
        ref TSink sink)
        where TSink : struct, ISymbolSink
    {
        bool useCache = cacheBits != 0;
        if (useCache)
        {
            Array.Clear(cache, 0, 1 << cacheBits);
        }

        int position = 0;
        int copyIndex = 0;
        while (position < argb.Length)
        {
            if (copyIndex < copies.Length && Vp8LMatchFinder.Position(copies[copyIndex]) == position)
            {
                ulong copy = copies[copyIndex++];
                int length = Vp8LMatchFinder.Length(copy);
                sink.Copy(position, length, mapper.ToCode(Vp8LMatchFinder.Distance(copy)));
                if (useCache)
                {
                    for (int k = position; k < position + length; k++)
                    {
                        cache[Vp8LBackwardReferences.CacheHash(argb[k], cacheBits)] = argb[k];
                    }
                }

                position += length;
                continue;
            }

            int start = position;
            int color = argb[position++];
            if (useCache)
            {
                int slot = Vp8LBackwardReferences.CacheHash(color, cacheBits);
                if (cache[slot] == color)
                {
                    sink.CacheHit(start, slot);
                    continue;
                }

                cache[slot] = color;
            }

            sink.Literal(start, (uint)color);
        }
    }

    // Walks the image into histograms (one flat set per group; the group of a symbol is the group
    // of the tile its first pixel is in), builds the codes and prices them exactly.
    private static StreamPlan Evaluate(
        ReadOnlySpan<int> argb,
        int width,
        ulong[] copyBuffer,
        int copyCount,
        Vp8LDistanceMapper mapper,
        int cacheBits,
        int[] cache,
        int groupBits,
        int tilesWide,
        int[]? tileGroups,
        int groupCount,
        bool isMain,
        bool keepHistograms = false)
    {
        var layout = new Vp8LHistogramLayout(LiteralAlphabet + LengthSymbols + (cacheBits == 0 ? 0 : 1 << cacheBits));
        int[][] sets = new int[groupCount][];
        for (int g = 0; g < sets.Length; g++)
        {
            sets[g] = new int[layout.Total];
        }

        var sink = new HistogramSink(sets, layout, width, groupBits, tilesWide, tileGroups);
        Walk(argb, copyBuffer.AsSpan(0, copyCount), mapper, cacheBits, cache, ref sink);

        long bits = sink.ExtraBits;
        var groups = new Vp8LPrefixCode[groupCount][];
        for (int g = 0; g < groupCount; g++)
        {
            groups[g] = new Vp8LPrefixCode[5];
            for (int i = 0; i < 5; i++)
            {
                (int start, int length) = layout.Segments[i];
                ReadOnlySpan<int> histogram = sets[g].AsSpan(start, length);
                groups[g][i] = Vp8LPrefixCode.Build(histogram);
                bits += groups[g][i].DataBits(histogram) + groups[g][i].HeaderBits();
            }
        }

        return new StreamPlan
        {
            IsMain = isMain,
            CacheBits = cacheBits,
            CopyBuffer = copyBuffer,
            CopyCount = copyCount,
            Mapper = mapper,
            GroupBits = groupBits,
            TileGroups = tileGroups,
            TilesWide = tilesWide,
            Groups = groups,
            CodeBits = bits,
            Histograms = keepHistograms ? sets : null,
        };
    }

    private struct HistogramSink(
        int[][] sets,
        Vp8LHistogramLayout layout,
        int width,
        int groupBits,
        int tilesWide,
        int[]? tileGroups) : ISymbolSink
    {
        public long ExtraBits;

        private readonly int[] SetOf(int position)
        {
            if (tileGroups is null)
            {
                return sets[0];
            }

            int y = position / width;
            int x = position - y * width;
            return sets[tileGroups[(y >> groupBits) * tilesWide + (x >> groupBits)]];
        }

        public void Literal(int position, uint argb)
        {
            int[] set = SetOf(position);
            set[layout.Alpha + (int)(argb >> 24)]++;
            set[layout.Red + (int)((argb >> 16) & 0xff)]++;
            set[(int)((argb >> 8) & 0xff)]++;
            set[layout.Blue + (int)(argb & 0xff)]++;
        }

        public void CacheHit(int position, int index) => SetOf(position)[LiteralAlphabet + LengthSymbols + index]++;

        public void Copy(int position, int length, int distanceCode)
        {
            int[] set = SetOf(position);
            Vp8LBackwardReferences.PrefixEncode(length, out int lengthSymbol, out int lengthExtra, out _);
            Vp8LBackwardReferences.PrefixEncode(distanceCode, out int distanceSymbol, out int distanceExtra, out _);
            set[LiteralAlphabet + lengthSymbol]++;
            set[layout.Distance + distanceSymbol]++;
            ExtraBits += lengthExtra + distanceExtra;
        }
    }

    private readonly struct WriteSink(
        Vp8LBitWriter writer,
        Vp8LPrefixCode[][] groups,
        int width,
        int groupBits,
        int tilesWide,
        int[]? tileGroups) : ISymbolSink
    {
        private Vp8LPrefixCode[] CodesOf(int position)
        {
            if (tileGroups is null)
            {
                return groups[0];
            }

            int y = position / width;
            int x = position - y * width;
            return groups[tileGroups[(y >> groupBits) * tilesWide + (x >> groupBits)]];
        }

        public void Literal(int position, uint argb)
        {
            // Each literal pixel is its four channels in the order green, red, blue, alpha.
            Vp8LPrefixCode[] codes = CodesOf(position);
            codes[0].WriteSymbol(writer, (int)((argb >> 8) & 0xff));
            codes[1].WriteSymbol(writer, (int)((argb >> 16) & 0xff));
            codes[2].WriteSymbol(writer, (int)(argb & 0xff));
            codes[3].WriteSymbol(writer, (int)(argb >> 24));
        }

        public void CacheHit(int position, int index) =>
            CodesOf(position)[0].WriteSymbol(writer, LiteralAlphabet + LengthSymbols + index);

        public void Copy(int position, int length, int distanceCode)
        {
            // The length prefix symbol (in the green code) and its extra bits, then the distance
            // prefix symbol and its extra bits.
            Vp8LPrefixCode[] codes = CodesOf(position);
            Vp8LBackwardReferences.PrefixEncode(length, out int lengthSymbol, out int lengthExtraBits, out int lengthExtra);
            codes[0].WriteSymbol(writer, LiteralAlphabet + lengthSymbol);
            writer.WriteBits((uint)lengthExtra, lengthExtraBits);
            Vp8LBackwardReferences.PrefixEncode(distanceCode, out int distanceSymbol, out int distanceExtraBits, out int distanceExtra);
            codes[4].WriteSymbol(writer, distanceSymbol);
            writer.WriteBits((uint)distanceExtra, distanceExtraBits);
        }
    }

    // ---- writing ----------------------------------------------------------------------------

    private static byte[] Write(int width, int height, bool alphaIsUsed, Prepared prepared, StreamPlan stream)
    {
        // Rough guess at the size, so a typical image does not regrow its buffer much.
        var writer = new Vp8LBitWriter((int)Math.Min((prepared.HeaderBits + stream.Bits) / 8 + 256, 1 << 26));

        writer.WriteBits(Signature, 8);
        writer.WriteBits((uint)(width - 1), 14);
        writer.WriteBits((uint)(height - 1), 14);
        writer.WriteBits(alphaIsUsed ? 1u : 0u, 1);
        writer.WriteBits(0, 3); // version

        int[] cache = new int[1 << Vp8LBackwardReferences.MaxCacheBits];
        foreach (TransformStep step in prepared.Steps)
        {
            writer.WriteBits(1, 1);
            writer.WriteBits((uint)step.Type, 2);
            switch (step.Type)
            {
                case PredictorType:
                case CrossColorType:
                    writer.WriteBits((uint)(step.Bits - 2), 3);
                    break;
                case ColorIndexingType:
                    writer.WriteBits((uint)(step.PaletteSize - 1), 8);
                    break;
            }

            if (step.Sub is not null)
            {
                WriteStream(writer, step.SubPixels!, step.SubWidth, step.Sub, cache);
            }
        }

        writer.WriteBits(0, 1); // no more transforms
        WriteStream(writer, prepared.Pixels, prepared.Width, stream, cache);
        return writer.Finish().ToArray();
    }

    // One entropy-coded image: the cache, the (main image only) meta prefix, the prefix codes and
    // the pixels.
    private static void WriteStream(Vp8LBitWriter writer, int[] argb, int width, StreamPlan stream, int[] cache)
    {
        if (stream.CacheBits == 0)
        {
            writer.WriteBits(0, 1); // no colour cache
        }
        else
        {
            writer.WriteBits(1, 1);
            writer.WriteBits((uint)stream.CacheBits, 4);
        }

        if (stream.IsMain)
        {
            if (stream.GroupBits == 0)
            {
                writer.WriteBits(0, 1); // no meta prefix image: one prefix code group for the whole image
            }
            else
            {
                writer.WriteBits(1, 1);
                writer.WriteBits((uint)(stream.GroupBits - 2), 3);
                WriteStream(writer, stream.EntropyPixels!, stream.EntropyWidth, stream.EntropyImage!, cache);
            }
        }

        foreach (Vp8LPrefixCode[] group in stream.Groups)
        {
            foreach (Vp8LPrefixCode code in group)
            {
                code.WriteHeader(writer);
            }
        }

        var sink = new WriteSink(writer, stream.Groups, width, stream.GroupBits, stream.TilesWide, stream.TileGroups);
        Walk(argb, stream.Copies, stream.Mapper, stream.CacheBits, cache, ref sink);
    }
}
