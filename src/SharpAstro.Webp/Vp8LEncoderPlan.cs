using System.Numerics;

namespace SharpAstro.Webp;

// Provenance: spec-derived from RFC 9649 only; the effort table is this encoder's own choice.

/// <summary>
/// What one encode will spend: the match finder's window and chain depth, which colour cache sizes
/// are tried, which transforms are tried and how many candidate pipelines get the full search, and
/// the working memory the match finder needs. The memory is worked out here, from
/// <c>width * height</c> and the effort, and checked against a budget before any array of it is
/// allocated: a plan that does not fit shrinks its window and finally gives up backward references
/// altogether (still a valid, literal-only encode) rather than allocating past the budget.
/// </summary>
internal readonly record struct Vp8LEncoderPlan(
    long PixelCount,
    int Effort,
    bool UseBackwardReferences,
    int WindowBits,
    int HashBits,
    int ChainDepth,
    int NiceLength,
    bool Lazy,
    int[] CacheBitsToTry,
    int MaxCopies,
    long WorkingBytes,
    int Finalists,
    bool Transforms = true,
    bool MetaPrefix = true)
{
    /// <summary>Bytes of scratch (histograms, code tables, the cache) that every plan charges.</summary>
    public const long FixedBytes = 1 << 20;

    /// <summary>
    /// Above this many pixels only one candidate pipeline is kept at a time, so the transformed
    /// copies of the image (four bytes a pixel each) stay a small multiple of the image itself.
    /// </summary>
    public const long LargeImagePixels = 1 << 22;

    /// <summary>The window in pixels (a power of two), or 0 without backward references.</summary>
    public int WindowSize => UseBackwardReferences ? 1 << WindowBits : 0;

    // Per effort 0..9. Effort 0 is the literal-only encoder: the same code path with nothing to
    // search and nothing to try.
    private static readonly int[] WindowBitsByEffort = [0, 14, 16, 18, 18, 20, 20, 20, 20, 20];
    private static readonly int[] ChainDepthByEffort = [0, 2, 4, 8, 16, 32, 64, 128, 256, 512];
    private static readonly int[] NiceLengthByEffort = [0, 16, 16, 32, 32, 64, 128, 256, 1024, 4096];

    // How many of the candidate pipelines (ranked by their cheap, literal-only cost) get the full
    // backward-reference and cache search. At effort 3 and up the untransformed image is one more.
    private static readonly int[] FinalistsByEffort = [0, 1, 1, 2, 2, 3, 3, 4, 5, 8];

    // Predictor and cross colour tile sizes (log2 of the side) tried per effort.
    private static readonly int[][] PredictorBitsByEffort =
    [
        [],
        [5],
        [5],
        [4],
        [4],
        [4, 5],
        [4, 5],
        [3, 4, 5, 6],
        [2, 3, 4, 5, 6],
        [2, 3, 4, 5, 6],
    ];

    private static readonly int[][] CacheBitsByEffort =
    [
        [0],
        [0],
        [0, 8],
        [0, 6, 10],
        [0, 4, 8, 10],
        [0, 2, 4, 6, 8, 10],
        [0, 2, 4, 6, 8, 10, 11],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
    ];

    /// <summary>
    /// The working memory a match finder with these settings allocates: the copy list (worst case
    /// one copy per <see cref="Vp8LMatchFinder.MinMatch"/> pixels, eight bytes each), the chain
    /// (four bytes per window slot), the hash heads, and <see cref="FixedBytes"/> of scratch.
    /// </summary>
    public static long BytesFor(long pixelCount, int windowBits, int hashBits) =>
        FixedBytes + 8L * MaxCopiesFor(pixelCount) + (4L << windowBits) + (4L << hashBits);

    /// <summary>The most copies a stream of <paramref name="pixelCount"/> pixels can hold.</summary>
    public static int MaxCopiesFor(long pixelCount) => (int)(pixelCount / Vp8LMatchFinder.MinMatch + 1);

    /// <summary>Makes the plan for an image of <paramref name="pixelCount"/> pixels, within <paramref name="budgetBytes"/>.</summary>
    public static Vp8LEncoderPlan Create(long pixelCount, int effort, long budgetBytes)
    {
        if (effort is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(effort), effort, "The effort must be 0 to 9.");
        }

        if (pixelCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelCount), pixelCount, "An image has at least one pixel.");
        }

        int[] caches = CacheBitsByEffort[effort];
        int finalists = pixelCount > LargeImagePixels ? Math.Min(1, FinalistsByEffort[effort]) : FinalistsByEffort[effort];
        if (effort == 0 || pixelCount < Vp8LMatchFinder.MinMatch)
        {
            return new Vp8LEncoderPlan(pixelCount, effort, false, 0, 0, 0, 0, false, effort == 0 ? [0] : caches, 0, FixedBytes, finalists);
        }

        // No point in a window or a hash table bigger than the image.
        int imageBits = BitOperations.Log2((uint)Math.Min(pixelCount, 1 << 28)) + 1;
        int windowBits = Math.Min(WindowBitsByEffort[effort], imageBits);
        int hashBits = Math.Clamp(imageBits + 1, 8, 16);
        while (windowBits > 10 && BytesFor(pixelCount, windowBits, hashBits) > budgetBytes)
        {
            windowBits--;
        }

        if (BytesFor(pixelCount, windowBits, hashBits) > budgetBytes)
        {
            return new Vp8LEncoderPlan(pixelCount, effort, false, 0, 0, 0, 0, false, [0], 0, FixedBytes, finalists);
        }

        return new Vp8LEncoderPlan(
            pixelCount,
            effort,
            true,
            windowBits,
            hashBits,
            ChainDepthByEffort[effort],
            NiceLengthByEffort[effort],
            effort >= 4,
            caches,
            MaxCopiesFor(pixelCount),
            BytesFor(pixelCount, windowBits, hashBits),
            finalists);
    }

    // ---- transform policy: one place that says how the effort trades time for size ----------------

    /// <summary>
    /// Whether any transform is tried (effort 1 and up; effort 0 is literal-only). A plan made with
    /// <c>Transforms</c> false (the tests' <c>plan with { Transforms = false }</c>) is the previous
    /// rung's configuration: no transform and no entropy image, only copies and the cache.
    /// <c>MetaPrefix</c> false turns off only the entropy image.
    /// </summary>
    public bool UseTransforms => Transforms && Effort >= 1;

    /// <summary>The predictor (and cross colour) tile side, as log2, sizes to try.</summary>
    public int[] PredictorBitsToTry => PredictorBitsByEffort[Effort];

    /// <summary>The finest step of the cross colour multiplier search: coarse at low effort, every value at 8 and up.</summary>
    public int CrossColorMinimumStep => Effort <= 4 ? 8 : Effort <= 7 ? 2 : 1;

    /// <summary>Whether subtract green is a candidate on its own and in front of the predictor.</summary>
    public bool TrySubtractGreen => Effort >= 1;

    /// <summary>Whether the cross colour transform is a candidate (effort 3 and up).</summary>
    public bool TryCrossColor => Effort >= 3;

    /// <summary>Whether the predictor without subtract green in front of it is a candidate (effort 6 and up).</summary>
    public bool TryPredictorAlone => Effort >= 6;

    /// <summary>Whether a palette ordered by first appearance is tried as well as the sorted one (effort 4 and up).</summary>
    public bool TryFirstSeenPalette => Effort >= 4;

    /// <summary>Whether a palette followed by the predictor is tried when the pixels are bundled (effort 5 and up).</summary>
    public bool TryPaletteThenPredictor => Effort >= 5;

    /// <summary>Whether the untransformed image is always one of the full-search finalists (effort 3 and up).</summary>
    public bool AlwaysTryUntransformed => Effort >= 3;

    /// <summary>Whether tiles are clustered into prefix-code groups (an entropy image) at all (effort 4 and up).</summary>
    public bool UseMetaPrefix => Transforms && MetaPrefix && Effort >= 4;

    /// <summary>The most tiles a clustering starts from; the tile size grows until the image fits.</summary>
    public int MetaTileCap => Effort <= 6 ? 1024 : 2048;

    /// <summary>The most prefix-code groups a clustering may produce.</summary>
    public int MetaMaxGroups => Effort <= 5 ? 16 : Effort <= 7 ? 64 : 256;

    /// <summary>How many partners each cluster samples per merge round.</summary>
    public int MetaCandidatesPerCluster => Effort <= 5 ? 3 : Effort <= 7 ? 6 : 12;

    /// <summary>Whether tiles are reassigned to their cheapest cluster after merging (effort 7 and up).</summary>
    public bool MetaRefine => Effort >= 7;

    /// <summary>
    /// The tile sizes (log2) to try for the entropy image of a <paramref name="width"/> x
    /// <paramref name="height"/> image: the smallest that stays within <see cref="MetaTileCap"/>
    /// tiles (the format allows 2 to 9, but this encoder starts at 4, or 3 from effort 8), and from
    /// effort 8 the next one up too.
    /// </summary>
    public int[] MetaBitsToTry(int width, int height)
    {
        // Tiles under 16x16 pixels (8x8 from effort 8) are too few symbols for a histogram to mean much.
        int bits = Effort >= 8 ? 3 : 4;
        while (bits < 9 && (long)Vp8LTransforms.TileCount(width, bits) * Vp8LTransforms.TileCount(height, bits) > MetaTileCap)
        {
            bits++;
        }

        return Effort >= 8 && bits < 9 ? [bits, bits + 1] : [bits];
    }
}
