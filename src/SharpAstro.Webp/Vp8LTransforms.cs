namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose of RFC 9649 section 4 (the transforms) only. The forward
// forms below are the inverses of what the decoder's Apply* methods undo; the search heuristics
// (entropy-estimated predictor modes and cross colour multipliers) are this encoder's own. No libwebp,
// libvpx or RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are black-box oracles.

/// <summary>
/// The forward half of the four VP8L transforms (RFC 9649 section 4): what an encoder applies so the
/// decoder's inverse gets the original pixels back exactly. Everything is modular byte arithmetic, so
/// a forward pass is lossless whatever the pixels are.
/// </summary>
internal static class Vp8LTransforms
{
    /// <summary>The predictor modes the format defines (0 to 13).</summary>
    public const int PredictorModes = 14;

    /// <summary>The colour used as the prediction of the top-left pixel and by mode 0: opaque black.</summary>
    public const int Black = unchecked((int)0xff000000);

    // ---- per-channel arithmetic -------------------------------------------------------------

    /// <summary>Per-channel <c>(a - b) mod 256</c>.</summary>
    public static int Sub(int a, int b) =>
        ((((a >> 24) - (b >> 24)) & 0xff) << 24) |
        ((((a >> 16) - (b >> 16)) & 0xff) << 16) |
        ((((a >> 8) - (b >> 8)) & 0xff) << 8) |
        ((a - b) & 0xff);

    /// <summary>Per-channel <c>floor((a + b) / 2)</c>: RFC 9649's Average2.</summary>
    public static int Average2(int a, int b) =>
        (int)((((uint)(a ^ b) & 0xfefefefeu) >> 1) + ((uint)a & (uint)b));

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static int ClampAddSubtractFull(int a, int b, int c) =>
        (Clamp((a >> 24 & 0xff) + (b >> 24 & 0xff) - (c >> 24 & 0xff)) << 24) |
        (Clamp((a >> 16 & 0xff) + (b >> 16 & 0xff) - (c >> 16 & 0xff)) << 16) |
        (Clamp((a >> 8 & 0xff) + (b >> 8 & 0xff) - (c >> 8 & 0xff)) << 8) |
        Clamp((a & 0xff) + (b & 0xff) - (c & 0xff));

    // The C-style integer division by 2 truncates toward zero, which is what the RFC's text means.
    private static int ClampAddSubtractHalf(int a, int b) =>
        (Clamp((a >> 24 & 0xff) + ((a >> 24 & 0xff) - (b >> 24 & 0xff)) / 2) << 24) |
        (Clamp((a >> 16 & 0xff) + ((a >> 16 & 0xff) - (b >> 16 & 0xff)) / 2) << 16) |
        (Clamp((a >> 8 & 0xff) + ((a >> 8 & 0xff) - (b >> 8 & 0xff)) / 2) << 8) |
        Clamp((a & 0xff) + ((a & 0xff) - (b & 0xff)) / 2);

    // RFC 9649's Select: the left pixel when the gradient estimate p = L + T - TL is nearer to it
    // (pL < pT, strictly), otherwise the top pixel, so a tie goes to the top.
    private static int Select(int left, int top, int topLeft)
    {
        int pL = Math.Abs((top >> 24 & 0xff) - (topLeft >> 24 & 0xff)) +
            Math.Abs((top >> 16 & 0xff) - (topLeft >> 16 & 0xff)) +
            Math.Abs((top >> 8 & 0xff) - (topLeft >> 8 & 0xff)) +
            Math.Abs((top & 0xff) - (topLeft & 0xff));
        int pT = Math.Abs((left >> 24 & 0xff) - (topLeft >> 24 & 0xff)) +
            Math.Abs((left >> 16 & 0xff) - (topLeft >> 16 & 0xff)) +
            Math.Abs((left >> 8 & 0xff) - (topLeft >> 8 & 0xff)) +
            Math.Abs((left & 0xff) - (topLeft & 0xff));
        return pL < pT ? left : top;
    }

    // ---- subtract green ---------------------------------------------------------------------

    /// <summary>Subtracts green from red and blue in place (RFC 9649 section 4.4, forward).</summary>
    public static void SubtractGreen(Span<int> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            int p = pixels[i];
            int green = (p >> 8) & 0xff;
            int red = (((p >> 16) & 0xff) - green) & 0xff;
            int blue = ((p & 0xff) - green) & 0xff;
            pixels[i] = (p & unchecked((int)0xff00ff00)) | (red << 16) | blue;
        }
    }

    // ---- predictor --------------------------------------------------------------------------

    /// <summary>
    /// The prediction of mode <paramref name="mode"/> (0 to 13) from the four neighbours of a pixel
    /// that is not on the top row or the left column.
    /// </summary>
    public static int Predict(int mode, int left, int top, int topLeft, int topRight) => mode switch
    {
        0 => Black,
        1 => left,
        2 => top,
        3 => topRight,
        4 => topLeft,
        5 => Average2(Average2(left, topRight), top),
        6 => Average2(left, topLeft),
        7 => Average2(left, top),
        8 => Average2(topLeft, top),
        9 => Average2(top, topRight),
        10 => Average2(Average2(left, topLeft), Average2(top, topRight)),
        11 => Select(left, top, topLeft),
        12 => ClampAddSubtractFull(left, top, topLeft),
        13 => ClampAddSubtractHalf(Average2(left, top), topLeft),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A predictor mode is 0 to 13."),
    };

    /// <summary>The number of tiles of <c>1 &lt;&lt; bits</c> pixels that cover <paramref name="size"/> pixels.</summary>
    public static int TileCount(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    /// <summary>
    /// Writes the predictor residuals of <paramref name="source"/> (<paramref name="width"/> x
    /// <paramref name="height"/>) into <paramref name="destination"/>. Every residual is taken
    /// against the ORIGINAL neighbours in <paramref name="source"/>, which is exactly what the
    /// decoder has rebuilt by the time it reaches the pixel. The top-left pixel is predicted by
    /// opaque black, the rest of the top row by its left neighbour and the rest of the left column
    /// by the pixel above, whatever the tile's mode says; the rightmost pixel of a row takes the
    /// leftmost pixel of its own row as the top-right neighbour.
    /// </summary>
    public static void PredictorForward(
        ReadOnlySpan<int> source,
        int width,
        int height,
        int bits,
        ReadOnlySpan<int> modes,
        Span<int> destination)
    {
        int tilesWide = TileCount(width, bits);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = row + x;
                int prediction;
                if (y == 0)
                {
                    prediction = x == 0 ? Black : source[index - 1];
                }
                else if (x == 0)
                {
                    prediction = source[index - width];
                }
                else
                {
                    int mode = modes[(y >> bits) * tilesWide + (x >> bits)];
                    prediction = Predict(
                        mode,
                        source[index - 1],
                        source[index - width],
                        source[index - width - 1],
                        source[index - width + 1]);
                }

                destination[index] = Sub(source[index], prediction);
            }
        }
    }

    /// <summary>The predictor sub-image pixel for <paramref name="mode"/>: the mode in green, opaque.</summary>
    public static int ModePixel(int mode) => Black | (mode << 8);

    /// <summary>
    /// Picks a mode per tile by the smallest entropy estimate of the tile's residuals (the four
    /// channels' histograms summed). <paramref name="forcedMode"/> of 0 to 13 uses that mode for
    /// every tile instead (a test hook). A tile with no pixel the mode could matter to (one row or
    /// column, or only edge pixels) gets mode 1.
    /// </summary>
    public static int[] ChoosePredictorModes(ReadOnlySpan<int> source, int width, int height, int bits, int forcedMode)
    {
        int tilesWide = TileCount(width, bits);
        int tilesHigh = TileCount(height, bits);
        int[] modes = new int[tilesWide * tilesHigh];
        if (forcedMode >= 0)
        {
            Array.Fill(modes, forcedMode);
            return modes;
        }

        int[] histogram = new int[4 * 256];
        int[] residuals = new int[1 << (2 * bits)];
        for (int ty = 0; ty < tilesHigh; ty++)
        {
            int y0 = Math.Max(1, ty << bits);
            int y1 = Math.Min(height, (ty + 1) << bits);
            for (int tx = 0; tx < tilesWide; tx++)
            {
                int x0 = Math.Max(1, tx << bits);
                int x1 = Math.Min(width, (tx + 1) << bits);
                int best = 1;
                double bestCost = double.MaxValue;
                if (x1 > x0 && y1 > y0)
                {
                    for (int mode = 0; mode < PredictorModes; mode++)
                    {
                        double cost = TileCost(source, width, x0, x1, y0, y1, mode, histogram, residuals);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = mode;
                        }
                    }
                }

                modes[ty * tilesWide + tx] = best;
            }
        }

        return modes;
    }

    private static double TileCost(
        ReadOnlySpan<int> source,
        int width,
        int x0,
        int x1,
        int y0,
        int y1,
        int mode,
        int[] histogram,
        int[] residuals)
    {
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int index = y * width + x;
                int residual = Sub(
                    source[index],
                    Predict(mode, source[index - 1], source[index - width], source[index - width - 1], source[index - width + 1]));
                residuals[n++] = residual;
                Bump(histogram, 0, residual & 0xff, ref s0);
                Bump(histogram, 256, (residual >> 8) & 0xff, ref s1);
                Bump(histogram, 512, (residual >> 16) & 0xff, ref s2);
                Bump(histogram, 768, (residual >> 24) & 0xff, ref s3);
            }
        }

        // Clear only the bins that were touched, so a small tile does not pay for 1024 bins.
        for (int i = 0; i < n; i++)
        {
            int residual = residuals[i];
            histogram[residual & 0xff] = 0;
            histogram[256 + ((residual >> 8) & 0xff)] = 0;
            histogram[512 + ((residual >> 16) & 0xff)] = 0;
            histogram[768 + ((residual >> 24) & 0xff)] = 0;
        }

        double total = Vp8LEntropy.NLog2N(n);
        return 4 * total - (s0 + s1 + s2 + s3);
    }

    // Adds one to a bin and keeps the running sum of count * log2(count) current.
    private static void Bump(int[] histogram, int offset, int value, ref double sum)
    {
        int count = histogram[offset + value];
        sum += Vp8LEntropy.NLog2N(count + 1) - Vp8LEntropy.NLog2N(count);
        histogram[offset + value] = count + 1;
    }

    // ---- cross colour -----------------------------------------------------------------------

    /// <summary>The cross colour sub-image pixel: red_to_blue, green_to_blue, green_to_red, opaque.</summary>
    public static int CrossColorPixel(int greenToRed, int greenToBlue, int redToBlue) =>
        Black | ((redToBlue & 0xff) << 16) | ((greenToBlue & 0xff) << 8) | (greenToRed & 0xff);

    // RFC 9649 section 4.2 ColorTransformDelta: both operands as signed bytes, then an arithmetic
    // shift right by 5.
    private static int Delta(int multiplier, int color) => ((sbyte)multiplier * (sbyte)color) >> 5;

    /// <summary>
    /// Applies the forward cross colour transform in place: red loses <c>green_to_red * green</c>
    /// and blue loses <c>green_to_blue * green + red_to_blue * red</c>, each product through
    /// <c>ColorTransformDelta</c> and using the ORIGINAL red, because the decoder adds the
    /// red_to_blue term back from the red it has already restored. <paramref name="tiles"/> holds one
    /// sub-image pixel per tile (see <see cref="CrossColorPixel"/>).
    /// </summary>
    public static void CrossColorForward(Span<int> pixels, int width, int height, int bits, ReadOnlySpan<int> tiles)
    {
        int tilesWide = TileCount(width, bits);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                int tile = tiles[(y >> bits) * tilesWide + (x >> bits)];
                int p = pixels[index];
                int green = (p >> 8) & 0xff;
                int red = (p >> 16) & 0xff;
                int blue = p & 0xff;
                int newRed = (red - Delta(tile, green)) & 0xff;
                int newBlue = (blue - Delta(tile >> 8, green) - Delta(tile >> 16, red)) & 0xff;
                pixels[index] = (p & unchecked((int)0xff00ff00)) | (newRed << 16) | newBlue;
            }
        }
    }

    /// <summary>
    /// Chooses the three multipliers of every tile by entropy estimate: green_to_red against the
    /// red channel's entropy, then green_to_blue against blue's, then red_to_blue against blue's
    /// again. Each is found by a coarse sweep of the signed byte range and then a halving
    /// refinement down to <paramref name="minimumStep"/>; a non-zero multiplier has to save a few
    /// bits over zero to be taken, so flat areas keep a constant sub-image.
    /// <paramref name="forced"/> (when not null) is used for every tile instead (a test hook).
    /// </summary>
    public static int[] ChooseCrossColor(
        ReadOnlySpan<int> pixels,
        int width,
        int height,
        int bits,
        int minimumStep,
        (int GreenToRed, int GreenToBlue, int RedToBlue)? forced)
    {
        int tilesWide = TileCount(width, bits);
        int tilesHigh = TileCount(height, bits);
        int[] tiles = new int[tilesWide * tilesHigh];
        if (forced is { } f)
        {
            Array.Fill(tiles, CrossColorPixel(f.GreenToRed, f.GreenToBlue, f.RedToBlue));
            return tiles;
        }

        int capacity = 1 << (2 * bits);
        int[] greens = new int[capacity];
        int[] reds = new int[capacity];
        int[] blues = new int[capacity];
        int[] values = new int[capacity];
        int[] histogram = new int[256];
        for (int ty = 0; ty < tilesHigh; ty++)
        {
            int y0 = ty << bits;
            int y1 = Math.Min(height, (ty + 1) << bits);
            for (int tx = 0; tx < tilesWide; tx++)
            {
                int x0 = tx << bits;
                int x1 = Math.Min(width, (tx + 1) << bits);
                int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int p = pixels[y * width + x];
                        greens[n] = (p >> 8) & 0xff;
                        reds[n] = (p >> 16) & 0xff;
                        blues[n] = p & 0xff;
                        n++;
                    }
                }

                int greenToRed = Search(
                    m =>
                    {
                        for (int i = 0; i < n; i++)
                        {
                            values[i] = (reds[i] - Delta(m, greens[i])) & 0xff;
                        }

                        return ChannelCost(values, n, histogram);
                    },
                    minimumStep);
                int greenToBlue = Search(
                    m =>
                    {
                        for (int i = 0; i < n; i++)
                        {
                            values[i] = (blues[i] - Delta(m, greens[i])) & 0xff;
                        }

                        return ChannelCost(values, n, histogram);
                    },
                    minimumStep);
                int redToBlue = Search(
                    m =>
                    {
                        for (int i = 0; i < n; i++)
                        {
                            values[i] = (blues[i] - Delta(greenToBlue, greens[i]) - Delta(m, reds[i])) & 0xff;
                        }

                        return ChannelCost(values, n, histogram);
                    },
                    minimumStep);
                tiles[ty * tilesWide + tx] = CrossColorPixel(greenToRed, greenToBlue, redToBlue);
            }
        }

        return tiles;
    }

    // Bits a channel's values cost: the entropy estimate, with the histogram left clean.
    private static double ChannelCost(int[] values, int n, int[] histogram)
    {
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            Bump(histogram, 0, values[i], ref sum);
        }

        for (int i = 0; i < n; i++)
        {
            histogram[values[i]] = 0;
        }

        return Vp8LEntropy.NLog2N(n) - sum;
    }

    private const double NonZeroPenaltyBits = 6;

    // The multiplier (-128 to 127) with the smallest cost. Zero is tried first and others have to
    // beat it by NonZeroPenaltyBits.
    private static int Search(Func<int, double> cost, int minimumStep)
    {
        int best = 0;
        double bestCost = cost(0);
        for (int value = -128; value <= 96; value += 32)
        {
            if (value == 0)
            {
                continue;
            }

            double c = cost(value) + NonZeroPenaltyBits;
            if (c < bestCost)
            {
                bestCost = c;
                best = value;
            }
        }

        for (int step = 16; step >= Math.Max(1, minimumStep); step >>= 1)
        {
            int center = best;
            foreach (int value in new[] { center - step, center + step })
            {
                if (value is < -128 or > 127)
                {
                    continue;
                }

                double c = cost(value) + (value == 0 ? 0 : NonZeroPenaltyBits);
                if (c < bestCost)
                {
                    bestCost = c;
                    best = value;
                }
            }
        }

        return best;
    }

    // ---- colour indexing --------------------------------------------------------------------

    /// <summary>
    /// How many bits a pixel-bundling shift is for a palette of <paramref name="paletteSize"/>
    /// colours (RFC 9649 section 4.4): 3 for two colours or fewer (eight pixels a byte), 2 for four
    /// or fewer, 1 for sixteen or fewer, else 0.
    /// </summary>
    public static int PaletteBits(int paletteSize) => paletteSize <= 2 ? 3 : paletteSize <= 4 ? 2 : paletteSize <= 16 ? 1 : 0;

    /// <summary>
    /// Collects the distinct colours of <paramref name="argb"/> in order of first appearance, giving
    /// up (false) when there are more than 256.
    /// </summary>
    public static bool TryCollectPalette(ReadOnlySpan<int> argb, out int[] colors)
    {
        // An open-addressing set of at most 256 colours in 1024 slots.
        int[] slotColor = new int[1024];
        bool[] slotUsed = new bool[1024];
        var found = new List<int>(16);
        foreach (int color in argb)
        {
            int slot = (int)(unchecked(0x9e3779b1u * (uint)color) >> 22);
            while (true)
            {
                if (!slotUsed[slot])
                {
                    if (found.Count == 256)
                    {
                        colors = [];
                        return false;
                    }

                    slotUsed[slot] = true;
                    slotColor[slot] = color;
                    found.Add(color);
                    break;
                }

                if (slotColor[slot] == color)
                {
                    break;
                }

                slot = (slot + 1) & 1023;
            }
        }

        colors = [.. found];
        return true;
    }

    /// <summary>
    /// Maps every pixel to its index in <paramref name="palette"/> and bundles the indices
    /// (<c>1 &lt;&lt; bits</c> a pixel) into green, least significant first, with alpha opaque and red
    /// and blue zero. The result is <c>TileCount(width, bits)</c> wide. The last pixel of a row that
    /// does not fill a bundle leaves the unused high bits zero.
    /// </summary>
    public static int[] IndexPixels(ReadOnlySpan<int> argb, int width, int height, ReadOnlySpan<int> palette, int bits)
    {
        var lookup = new Dictionary<int, int>(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            lookup[palette[i]] = i;
        }

        int perPixel = 1 << bits;
        int bitsPerIndex = 8 >> bits;
        int packedWidth = TileCount(width, bits);
        int[] packed = new int[packedWidth * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = lookup[argb[y * width + x]];
                int target = y * packedWidth + (x >> bits);
                packed[target] |= index << (8 + (x & (perPixel - 1)) * bitsPerIndex);
            }
        }

        for (int i = 0; i < packed.Length; i++)
        {
            packed[i] |= Black;
        }

        return packed;
    }

    /// <summary>The palette's sub-image: each colour minus the one before it (the first minus 0), per channel.</summary>
    public static int[] DeltaCodePalette(ReadOnlySpan<int> palette)
    {
        int[] deltas = new int[palette.Length];
        int previous = 0;
        for (int i = 0; i < palette.Length; i++)
        {
            deltas[i] = Sub(palette[i], previous);
            previous = palette[i];
        }

        return deltas;
    }
}
