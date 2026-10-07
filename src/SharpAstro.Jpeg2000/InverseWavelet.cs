using System;

namespace SharpAstro.Jpeg2000;

/// <summary>
/// The inverse discrete wavelet transform of T.800 Annex F: the reversible 5/3
/// filter in integers, and the irreversible 9/7 filter in single-precision floats.
/// <para>
/// Reconstruction climbs the resolutions: level 0's LL band is the starting
/// image, and each level up interleaves that image with the level's HL, LH and
/// HH bands and filters the result, first along rows and then along columns.
/// </para>
/// <para>
/// Everything is done in absolute coordinates. F.3.3's interleave places a
/// sample by the <em>parity</em> of its index on the resolution grid, so an
/// image whose origin is not at zero interleaves differently from one that is,
/// and a version of this that worked in widths and heights would decode a
/// correct-looking but shifted picture for any codestream with a non-zero
/// <c>XOsiz</c>.
/// </para>
/// </summary>
internal static class InverseWavelet
{
    /// <summary>
    /// Reconstructs the tile-component's samples from its decoded subband
    /// coefficients.
    /// </summary>
    /// <returns>The samples, row-major over the tile-component's bounds.</returns>
    public static int[] Reconstruct(TileComponent tile)
    {
        // Resolution 0 is the LL band verbatim — for a codestream with no
        // decomposition at all, this is the whole answer.
        var lowPass = tile.Resolutions[0].Bands[0];
        var current = (int[])lowPass.Coefficients.Clone();
        var currentBounds = lowPass.Bounds;

        for (var r = 1; r < tile.Resolutions.Length; r++)
        {
            var resolution = tile.Resolutions[r];
            current = Lift(current, currentBounds, resolution);
            currentBounds = resolution.Bounds;
        }

        return current;
    }

    /// <summary>
    /// <see cref="Reconstruct"/> for a 9/7 tile-component: the same climb over the
    /// same interleave, from the dequantized coefficients, in floats.
    /// <para>
    /// Single precision, deliberately. The 9/7 coefficients are irrational, so no
    /// width makes this exact, and the reference decoders compute in floats too:
    /// matching their arithmetic is what keeps the difference from them to the
    /// rounding of the last step rather than a drift built up over five levels.
    /// </para>
    /// </summary>
    /// <returns>The samples, row-major over the tile-component's bounds, before rounding.</returns>
    public static float[] ReconstructIrreversible(TileComponent tile)
    {
        var lowPass = tile.Resolutions[0].Bands[0];
        var current = (float[])lowPass.Values.Clone();
        var currentBounds = lowPass.Bounds;

        for (var r = 1; r < tile.Resolutions.Length; r++)
        {
            var resolution = tile.Resolutions[r];
            current = LiftIrreversible(current, currentBounds, resolution);
            currentBounds = resolution.Bounds;
        }

        return current;
    }

    /// <summary>
    /// <see cref="Lift"/> for the 9/7 filter: the same interleave and the same row-then-column
    /// order, over floats.
    /// </summary>
    private static float[] LiftIrreversible(float[] lowPass, Rect lowPassBounds, Resolution resolution)
    {
        var bounds = resolution.Bounds;
        var width = bounds.Width;
        var height = bounds.Height;
        var samples = new float[(long)width * height];
        if (width == 0 || height == 0) return samples;

        Scatter(samples, bounds, lowPass, lowPassBounds, xOdd: false, yOdd: false);
        foreach (var band in resolution.Bands)
        {
            switch (band.Kind)
            {
                case BandKind.Hl:
                    Scatter(samples, bounds, band.Values, band.Bounds, xOdd: true, yOdd: false);
                    break;
                case BandKind.Lh:
                    Scatter(samples, bounds, band.Values, band.Bounds, xOdd: false, yOdd: true);
                    break;
                case BandKind.Hh:
                    Scatter(samples, bounds, band.Values, band.Bounds, xOdd: true, yOdd: true);
                    break;
            }
        }

        var scratch = new float[Math.Max(width, height) + 2 * IrreversibleMargin];
        var line = new float[Math.Max(width, height)];

        for (var y = 0; y < height; y++)
        {
            Array.Copy(samples, (long)y * width, line, 0, width);
            Filter97(line.AsSpan(0, width), bounds.X0, bounds.X1, scratch);
            Array.Copy(line, 0, samples, (long)y * width, width);
        }

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++) line[y] = samples[(long)y * width + x];
            Filter97(line.AsSpan(0, height), bounds.Y0, bounds.Y1, scratch);
            for (var y = 0; y < height; y++) samples[(long)y * width + x] = line[y];
        }

        return samples;
    }

    // T.800 Table F.4: the 9/7 lifting parameters and scaling factor.
    private const float Alpha = -1.586134342059924f;
    private const float Beta = -0.052980118572961f;
    private const float Gamma = 0.882911075530934f;
    private const float Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f;
    private const float InverseK = 1f / K;

    /// <summary>
    /// How far the signal is extended past each end before filtering. Each of the four lifting
    /// steps reads one neighbour each way, so a value computed next to the end of the extended
    /// buffer, where one neighbour is missing, is wrong, and the wrongness moves one sample
    /// inward per step: four steps, four samples. With this margin every wrong value is in the
    /// extension and none is in the signal.
    /// </summary>
    private const int IrreversibleMargin = 4;

    /// <summary>
    /// T.800 F.3.7 (1D_SR) with the irreversible filter of F.3.8.2, over the half-open index range
    /// <c>[i0, i1)</c>: undo the scaling (the low-pass samples by K, the high-pass by 1/K), then
    /// the four lifting steps in reverse, each subtracting what the forward transform added.
    /// </summary>
    private static void Filter97(Span<float> signal, int i0, int i1, float[] scratch)
    {
        var length = i1 - i0;
        if (length <= 0) return;

        if (length == 1)
        {
            // F.3.7's degenerate case, as in Filter: a lone low-pass sample passes through.
            if ((i0 & 1) == 0) return;

            throw new NotSupportedException(
                "JPEG 2000: a subband one sample wide starting at an odd coordinate is not implemented. " +
                "It needs an odd image or tile origin, which opj_compress cannot emit, so this path has " +
                "no reference to be validated against.");
        }

        var origin = i0 - IrreversibleMargin;
        var count = length + 2 * IrreversibleMargin;
        var x = scratch.AsSpan(0, count);
        for (var j = 0; j < count; j++) x[j] = signal[Mirror(origin + j, i0, i1) - i0];

        // x[j] holds the sample at absolute index origin + j, and which samples are low-pass is
        // decided by the parity of that ABSOLUTE index, not of j.
        var firstEven = (origin & 1) == 0 ? 0 : 1;
        var firstOdd = 1 - firstEven;

        // Steps 1 and 2.
        for (var j = firstEven; j < count; j += 2) x[j] *= K;
        for (var j = firstOdd; j < count; j += 2) x[j] *= InverseK;

        // Steps 3 to 6, each over every sample of its parity that has both neighbours.
        Lift97(x, firstEven, Delta);
        Lift97(x, firstOdd, Gamma);
        Lift97(x, firstEven, Beta);
        Lift97(x, firstOdd, Alpha);

        x.Slice(IrreversibleMargin, length).CopyTo(signal);
    }

    /// <summary>One lifting step: every sample of one parity less <paramref name="c"/> times the sum of its neighbours.</summary>
    private static void Lift97(Span<float> x, int first, float c)
    {
        var start = first == 0 ? 2 : first;
        for (var j = start; j + 1 < x.Length; j += 2) x[j] -= c * (x[j - 1] + x[j + 1]);
    }

    /// <summary>
    /// T.800 F.3.2 (2D_SR) for one resolution level: interleave, filter the rows,
    /// then filter the columns.
    /// </summary>
    private static int[] Lift(int[] lowPass, Rect lowPassBounds, Resolution resolution)
    {
        var bounds = resolution.Bounds;
        var width = bounds.Width;
        var height = bounds.Height;
        var samples = new int[(long)width * height];
        if (width == 0 || height == 0) return samples;

        // F.3.3 (2D_INTERLEAVE). The four bands land on the four parities of the
        // resolution grid: LL on even/even, HL on odd/even, LH on even/odd, HH on
        // odd/odd — in ABSOLUTE coordinates, so which band a given cell takes
        // depends on where the image sits, not merely on its size.
        Scatter(samples, bounds, lowPass, lowPassBounds, xOdd: false, yOdd: false);
        foreach (var band in resolution.Bands)
        {
            switch (band.Kind)
            {
                case BandKind.Hl:
                    Scatter(samples, bounds, band.Coefficients, band.Bounds, xOdd: true, yOdd: false);
                    break;
                case BandKind.Lh:
                    Scatter(samples, bounds, band.Coefficients, band.Bounds, xOdd: false, yOdd: true);
                    break;
                case BandKind.Hh:
                    Scatter(samples, bounds, band.Coefficients, band.Bounds, xOdd: true, yOdd: true);
                    break;
            }
        }

        // F.3.4 HOR_SR then F.3.5 VER_SR. The order is fixed by the spec and is
        // not a free choice: the 5/3 lifting steps do not commute across
        // dimensions.
        var scratch = new int[Math.Max(width, height) + 8];
        var line = new int[Math.Max(width, height)];

        for (var y = 0; y < height; y++)
        {
            Array.Copy(samples, (long)y * width, line, 0, width);
            Filter(line.AsSpan(0, width), bounds.X0, bounds.X1, scratch);
            Array.Copy(line, 0, samples, (long)y * width, width);
        }

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++) line[y] = samples[(long)y * width + x];
            Filter(line.AsSpan(0, height), bounds.Y0, bounds.Y1, scratch);
            for (var y = 0; y < height; y++) samples[(long)y * width + x] = line[y];
        }

        return samples;
    }

    /// <summary>
    /// Places one band's coefficients on the interleaved grid at the parity its
    /// orientation dictates.
    /// </summary>
    private static void Scatter<T>(
        T[] destination, Rect destinationBounds, T[] source, Rect sourceBounds, bool xOdd, bool yOdd)
    {
        if (sourceBounds.IsEmpty) return;

        var width = destinationBounds.Width;
        var sourceWidth = sourceBounds.Width;

        for (var v = sourceBounds.Y0; v < sourceBounds.Y1; v++)
        {
            // A band sample at index v sits at 2v (+1 for a high-pass band) on
            // the resolution grid.
            var y = 2 * v + (yOdd ? 1 : 0) - destinationBounds.Y0;
            if ((uint)y >= (uint)destinationBounds.Height) continue;

            var sourceRow = (v - sourceBounds.Y0) * sourceWidth;
            var destinationRow = y * width;

            for (var u = sourceBounds.X0; u < sourceBounds.X1; u++)
            {
                var x = 2 * u + (xOdd ? 1 : 0) - destinationBounds.X0;
                if ((uint)x >= (uint)width) continue;

                destination[destinationRow + x] = source[sourceRow + (u - sourceBounds.X0)];
            }
        }
    }

    /// <summary>
    /// T.800 F.3.7 (1D_SR) with the reversible filter of F.3.8.2, over the
    /// half-open index range <c>[i0, i1)</c>.
    /// </summary>
    private static void Filter(Span<int> signal, int i0, int i1, int[] scratch)
    {
        var length = i1 - i0;
        if (length <= 0) return;

        if (length == 1)
        {
            // F.3.7's degenerate case. An even index is a lone low-pass sample
            // and passes through untouched; that happens for any image one
            // sample wide, so it is ordinary and reachable.
            if ((i0 & 1) == 0) return;

            // An odd index is a lone HIGH-pass sample. The spec halves it, but
            // reaching this needs an odd image origin, which nothing available
            // here can encode — so there is no way to check it against the
            // reference. Refusing beats emitting a number no oracle has ever
            // confirmed; the whole family's discipline.
            throw new NotSupportedException(
                "JPEG 2000: a subband one sample wide starting at an odd coordinate is not implemented. " +
                "It needs an odd image or tile origin, which opj_compress cannot emit, so this path has " +
                "no reference to be validated against.");
        }

        // Work over a margin-padded copy so the symmetric extension of F.3.6 can
        // be materialised once rather than tested for on every access.
        const int margin = 2;
        var y = scratch;
        for (var j = 0; j < length + 2 * margin; j++)
        {
            y[j] = signal[Mirror(i0 - margin + j, i0, i1) - i0];
        }

        var low = FloorHalf(i0);
        var high = FloorHalf(i1);
        var x = new int[length + 2 * margin];
        Array.Copy(y, x, length + 2 * margin);

        var origin = i0 - margin;

        // F.3.8.2, step 1: every even-indexed sample, undoing the update.
        for (var n = low; n <= high; n++)
        {
            var index = 2 * n - origin;
            if ((uint)index >= (uint)x.Length) continue;

            // >> is an arithmetic shift, which IS the floor division the spec
            // writes; `/ 4` would round toward zero and be wrong for negatives.
            x[index] = y[index] - ((y[index - 1] + y[index + 1] + 2) >> 2);
        }

        // F.3.8.2, step 2: every odd-indexed sample, from the even ones just
        // recovered.
        for (var n = low; n < high; n++)
        {
            var index = 2 * n + 1 - origin;
            if ((uint)index >= (uint)x.Length) continue;

            x[index] = y[index] + ((x[index - 1] + x[index + 1]) >> 1);
        }

        for (var j = 0; j < length; j++) signal[j] = x[margin + j];
    }

    /// <summary>
    /// Whole-point symmetric extension (T.800 F.3.6): reflect about <c>i0</c> and
    /// <c>i1 - 1</c> without repeating the edge sample.
    /// </summary>
    private static int Mirror(int index, int i0, int i1)
    {
        var period = 2 * (i1 - i0 - 1);
        if (period <= 0) return i0;

        var k = (index - i0) % period;
        if (k < 0) k += period;

        return i0 + (k >= i1 - i0 ? period - k : k);
    }

    /// <summary>Floor of half, correct for negative values (<c>>></c>, not <c>/</c>).</summary>
    private static int FloorHalf(int value) => value >> 1;

    /// <summary>
    /// Runs one 1D synthesis over an interleaved signal, for tests that need to
    /// check the lifting arithmetic without building a codestream around it.
    /// </summary>
    internal static int[] FilterForTests(int[] interleaved, int i0, int i1)
    {
        var signal = (int[])interleaved.Clone();
        Filter(signal.AsSpan(), i0, i1, new int[signal.Length + 8]);
        return signal;
    }

    /// <summary>The 9/7 counterpart of <see cref="FilterForTests"/>.</summary>
    internal static float[] Filter97ForTests(float[] interleaved, int i0, int i1)
    {
        var signal = (float[])interleaved.Clone();
        Filter97(signal.AsSpan(), i0, i1, new float[signal.Length + 2 * IrreversibleMargin]);
        return signal;
    }
}
