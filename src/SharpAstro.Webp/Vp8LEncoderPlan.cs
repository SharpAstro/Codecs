using System.Numerics;

namespace SharpAstro.Webp;

// Provenance: spec-derived from RFC 9649 only; the effort table is this encoder's own choice.

/// <summary>
/// What one encode will spend: the match finder's window and chain depth, which colour cache sizes
/// are tried, and the working memory all of that needs. The memory is worked out here, from
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
    long WorkingBytes)
{
    /// <summary>Bytes of scratch (histograms, code tables, the cache) that every plan charges.</summary>
    public const long FixedBytes = 1 << 20;

    /// <summary>The window in pixels (a power of two), or 0 without backward references.</summary>
    public int WindowSize => UseBackwardReferences ? 1 << WindowBits : 0;

    // Per effort 0..9. Effort 0 is the literal-only encoder: the same code path with nothing to
    // search and nothing to try.
    private static readonly int[] WindowBitsByEffort = [0, 14, 16, 18, 18, 20, 20, 20, 20, 20];
    private static readonly int[] ChainDepthByEffort = [0, 2, 4, 8, 16, 32, 64, 128, 256, 512];
    private static readonly int[] NiceLengthByEffort = [0, 16, 16, 32, 32, 64, 128, 256, 1024, 4096];

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
        if (effort == 0 || pixelCount < Vp8LMatchFinder.MinMatch)
        {
            return new Vp8LEncoderPlan(pixelCount, effort, false, 0, 0, 0, 0, false, effort == 0 ? [0] : caches, 0, FixedBytes);
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
            return new Vp8LEncoderPlan(pixelCount, effort, false, 0, 0, 0, 0, false, [0], 0, FixedBytes);
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
            BytesFor(pixelCount, windowBits, hashBits));
    }
}
