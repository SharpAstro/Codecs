using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

// The third encoder rung: the four VP8L transforms (subtract green, predictor, cross colour, colour
// indexing), the entropy image, and the choice between candidate pipelines. Spec-derived from
// RFC 9649; the decoder's inverse transforms are the reference for what each forward form must
// undo, and dwebp is the black-box oracle (WebpLosslessEncoderTransformOracleTests).

/// <summary>Images the transform tests share.</summary>
internal static class WebpLosslessEncoderTransformImages
{
    public static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>Random ARGB with every channel's extremes over-represented, so clamps and wraps happen.</summary>
    public static int[] Random(int width, int height, uint seed)
    {
        uint s = seed | 1;
        int[] argb = new int[width * height];
        for (int i = 0; i < argb.Length; i++)
        {
            int p = 0;
            for (int c = 0; c < 4; c++)
            {
                uint r = Next(ref s);
                int v = (r & 7) switch { 0 => 0, 1 => 255, 2 => 254, 3 => 1, _ => (int)((r >> 8) & 0xff) };
                p = (p << 8) | v;
            }

            argb[i] = p;
        }

        return argb;
    }

    /// <summary>A smooth image with noise: what the predictor and the cross colour transform are for.</summary>
    public static int[] Smooth(int width, int height, uint seed, bool alpha)
    {
        uint s = seed | 1;
        int[] argb = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int g = Math.Clamp((x * 3 + y * 2) / 2 + (int)(Next(ref s) % 5), 0, 255);
                int r = Math.Clamp(g * 3 / 4 + 20 + (int)(Next(ref s) % 3), 0, 255);
                int b = Math.Clamp(255 - g / 2 + (int)(Next(ref s) % 3), 0, 255);
                int a = alpha ? Math.Clamp(255 - x * 255 / Math.Max(1, width - 1), 0, 255) : 255;
                argb[y * width + x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }

        return argb;
    }

    /// <summary>An image with exactly <paramref name="colours"/> distinct colours (fewer only if the image is smaller).</summary>
    public static int[] Colours(int width, int height, int colours, uint seed, bool alpha)
    {
        uint s = seed | 1;
        int[] palette = new int[colours];
        for (int i = 0; i < colours; i++)
        {
            // Distinct by construction: the index is in the blue channel.
            int a = alpha ? (int)(Next(ref s) & 0xff) : 255;
            palette[i] = (a << 24) | ((int)(Next(ref s) & 0xff) << 16) | ((int)(Next(ref s) & 0xff) << 8) | i;
        }

        int[] argb = new int[width * height];
        for (int p = 0; p < argb.Length; p++)
        {
            // The first `colours` pixels walk the palette so every colour occurs.
            argb[p] = palette[p < colours ? p : (int)(Next(ref s) % (uint)colours)];
        }

        return argb;
    }

    public static byte[] ToRgba(int[] argb)
    {
        byte[] rgba = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++)
        {
            rgba[i * 4] = (byte)(argb[i] >> 16);
            rgba[i * 4 + 1] = (byte)(argb[i] >> 8);
            rgba[i * 4 + 2] = (byte)argb[i];
            rgba[i * 4 + 3] = (byte)(argb[i] >> 24);
        }

        return rgba;
    }

    /// <summary>Encodes one forced candidate and wraps it in a file.</summary>
    public static byte[] Encode(
        int[] argb,
        int width,
        int height,
        Vp8LPipeline? pipeline,
        int effort = 6,
        int cacheBits = 0,
        bool copies = false,
        int metaBits = 0)
    {
        var plan = Vp8LEncoderPlan.Create((long)width * height, effort, WebpLimits.MaximumEncoderWorkingBytes);
        bool alpha = argb.Any(static p => (p >>> 24) != 255);
        byte[] vp8l = Vp8LEncoder.EncodeCandidate(argb, width, height, alpha, plan, cacheBits, copies, pipeline, metaBits);
        return WebpWriter.Write(vp8l, width, height, alpha, null, null, null);
    }

    /// <summary>The first transform marker and type of a simple-layout file, or -1 for none.</summary>
    public static int FirstTransform(byte[] file)
    {
        const int payload = 20;
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

        return Read(1) == 0 ? -1 : Read(2);
    }
}

public sealed class WebpLosslessEncoderTransformForwardTests
{
    private static Vp8LDecoder.Transform Transform(int type, int width, int height, int bits, int[]? data) =>
        new(type, width, height, bits, data, null, 0);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)]
    [InlineData(33, 17)]
    public void SubtractGreenIsUndoneByTheDecodersAddGreen(int width, int height)
    {
        int[] original = WebpLosslessEncoderTransformImages.Random(width, height, 11);
        int[] pixels = (int[])original.Clone();
        Vp8LTransforms.SubtractGreen(pixels);
        Assert.False(pixels.AsSpan().SequenceEqual(original) && original.Any(static p => ((p >> 8) & 0xff) != 0));
        Vp8LDecoder.ApplySubtractGreenScalar(pixels, Transform(2, width, height, 0, null));
        Assert.Equal(original, pixels);
    }

    // The RFC's rules written out a second time, per pixel, from the ORIGINAL array.
    private static int Reference(int[] source, int width, int x, int y, int mode)
    {
        if (x == 0 && y == 0)
        {
            return unchecked((int)0xff000000);
        }

        if (y == 0)
        {
            return source[y * width + x - 1];
        }

        if (x == 0)
        {
            return source[(y - 1) * width + x];
        }

        int l = source[y * width + x - 1];
        int t = source[(y - 1) * width + x];
        int tl = source[(y - 1) * width + x - 1];

        // The top-right of the rightmost pixel is the leftmost pixel of the current row, which is
        // the next pixel in memory after the top row's last.
        int tr = source[(y - 1) * width + x + 1];
        return Vp8LTransforms.Predict(mode, l, t, tl, tr);
    }

    [Theory]
    [InlineData(1, 9, 2)]
    [InlineData(9, 1, 2)]
    [InlineData(2, 2, 2)]
    [InlineData(13, 11, 2)]
    [InlineData(33, 17, 3)]
    [InlineData(70, 45, 4)]
    [InlineData(70, 45, 6)]
    public void EveryModeInvertsThroughTheDecoderAtTileEdgesThatAreNotMultiples(int width, int height, int bits)
    {
        int[] original = WebpLosslessEncoderTransformImages.Random(width, height, 17);
        for (int mode = 0; mode < Vp8LTransforms.PredictorModes; mode++)
        {
            int[] modes = Vp8LTransforms.ChoosePredictorModes(original, width, height, bits, mode);
            int[] residuals = new int[original.Length];
            Vp8LTransforms.PredictorForward(original, width, height, bits, modes, residuals);
            int[] modePixels = modes.Select(Vp8LTransforms.ModePixel).ToArray();
            int[] restored = Vp8LDecoder.ApplyPredictorScalar(
                (int[])residuals.Clone(), Transform(0, width, height, bits, modePixels));
            Assert.True(original.AsSpan().SequenceEqual(restored), $"mode {mode}, {width}x{height}, bits {bits}");
        }
    }

    [Theory]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    [InlineData(34, 21)]
    public void ResidualsAreTakenAgainstTheOriginalNeighboursAtEveryPixel(int width, int height)
    {
        int[] original = WebpLosslessEncoderTransformImages.Random(width, height, 23);
        for (int mode = 0; mode < Vp8LTransforms.PredictorModes; mode++)
        {
            int[] modes = Vp8LTransforms.ChoosePredictorModes(original, width, height, 3, mode);
            int[] residuals = new int[original.Length];
            Vp8LTransforms.PredictorForward(original, width, height, 3, modes, residuals);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int expected = Vp8LTransforms.Sub(original[y * width + x], Reference(original, width, x, y, mode));
                    Assert.True(expected == residuals[y * width + x], $"mode {mode} at ({x}, {y})");
                }
            }
        }
    }

    [Fact]
    public void ModeZeroOneAndTwoAreOpaqueBlackLeftAndTop()
    {
        int l = 0x11223344, t = 0x55667788, tl = 0x01020304, tr = 0x0a0b0c0d;
        Assert.Equal(unchecked((int)0xff000000), Vp8LTransforms.Predict(0, l, t, tl, tr));
        Assert.Equal(l, Vp8LTransforms.Predict(1, l, t, tl, tr));
        Assert.Equal(t, Vp8LTransforms.Predict(2, l, t, tl, tr));
        Assert.Equal(tr, Vp8LTransforms.Predict(3, l, t, tl, tr));
        Assert.Equal(tl, Vp8LTransforms.Predict(4, l, t, tl, tr));
    }

    [Fact]
    public void AveragesRoundDownPerChannel()
    {
        Assert.Equal(0x01020304, Vp8LTransforms.Average2(0x01020304, 0x01020304));

        // alpha (0 + 1) / 2 = 0, red (0 + 3) / 2 = 1, green (0 + 3) / 2 = 1, blue (0 + 3) / 2 = 1
        Assert.Equal(0x00010101, Vp8LTransforms.Average2(0x00000000, 0x01030303));
        Assert.Equal(unchecked((int)0x7f7f7f7f), Vp8LTransforms.Average2(unchecked((int)0xffffffff), 0x00000000));
    }

    [Fact]
    public void SelectChoosesTheLeftPixelOnlyWhenStrictlyNearerAndTheTopPixelOnATie()
    {
        // pL = sum |top - topLeft|, pT = sum |left - topLeft|; left wins only if pL < pT.
        int topLeft = 0;
        int top = 0x00000005;   // blue 5 away from the top-left
        int leftTied = 0x00000500; // green 5 away: pL == pT
        Assert.Equal(top, Vp8LTransforms.Predict(11, leftTied, top, topLeft, 0));

        int leftFar = 0x00000a00;  // pT = 10, pL = 5: pL < pT, so left
        Assert.Equal(leftFar, Vp8LTransforms.Predict(11, leftFar, top, topLeft, 0));

        int leftNear = 0x00000003; // pT = 3, pL = 5: not pL < pT, so top
        Assert.Equal(top, Vp8LTransforms.Predict(11, leftNear, top, topLeft, 0));
    }

    [Fact]
    public void ClampAddSubtractFullClampsBothWays()
    {
        // l + t - tl per channel, clamped to 0..255.
        Assert.Equal(unchecked((int)0xffffffff), Vp8LTransforms.Predict(12, unchecked((int)0xf0f0f0f0), unchecked((int)0xf0f0f0f0), 0, 0));
        Assert.Equal(0, Vp8LTransforms.Predict(12, 0x10101010, 0x10101010, unchecked((int)0xf0f0f0f0), 0));
        Assert.Equal(0x20406080, Vp8LTransforms.Predict(12, 0x10203040, 0x10203040, 0x00000000, 0));
    }

    [Fact]
    public void ClampAddSubtractHalfTruncatesTowardZeroThenClamps()
    {
        // a = Average2(l, t) = l when l == t; result = clamp(a + (a - tl) / 2), division truncating.
        int Blue(int l, int tl) => Vp8LTransforms.Predict(13, l, l, tl, 0) & 0xff;
        Assert.Equal(12, Blue(10, 5));    // 10 + 5/2 = 12
        Assert.Equal(3, Blue(5, 10));     // 5 + (-5)/2 = 5 - 2 = 3, not 2 (which floor division would give)
        Assert.Equal(255, Blue(250, 0));  // 250 + 125 clamps
        Assert.Equal(0, Blue(0, 250));    // 0 - 125 clamps
    }

    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(7, 5, 2)]
    [InlineData(40, 33, 3)]
    public void CrossColorWithExtremeMultipliersIsUndoneByTheDecoder(int width, int height, int bits)
    {
        int[] original = WebpLosslessEncoderTransformImages.Random(width, height, 29);
        foreach ((int gr, int gb, int rb) in new[] { (0, 0, 0), (-128, 127, -128), (127, -128, 127), (31, -17, 64), (-1, 1, -1) })
        {
            int[] tiles = Vp8LTransforms.ChooseCrossColor(original, width, height, bits, 1, (gr, gb, rb));
            int[] pixels = (int[])original.Clone();
            Vp8LTransforms.CrossColorForward(pixels, width, height, bits, tiles);
            int[] restored = Vp8LDecoder.ApplyColorTransformScalar(pixels, Transform(1, width, height, bits, tiles));
            Assert.True(original.AsSpan().SequenceEqual(restored), $"({gr}, {gb}, {rb}) at {width}x{height}");
        }
    }

    [Fact]
    public void CrossColorPutsTheMultipliersInBlueGreenAndRedOfTheSubImagePixel()
    {
        // RFC 9649 section 4.2: red_to_blue in red, green_to_blue in green, green_to_red in blue.
        Assert.Equal(unchecked((int)0xff0a0b0c), Vp8LTransforms.CrossColorPixel(0x0c, 0x0b, 0x0a));
        Assert.Equal(unchecked((int)0xffff807f), Vp8LTransforms.CrossColorPixel(0x7f, -128, -1));
    }

    [Fact]
    public void CrossColorSubtractsTheProductsAndUsesTheOriginalRed()
    {
        // green_to_red = 32 (one whole green), green_to_blue = 0, red_to_blue = 32 (one whole red):
        // red' = red - green, blue' = blue - red, with red the ORIGINAL red.
        int[] pixels = [(0xff << 24) | (100 << 16) | (40 << 8) | 90];
        int[] tiles = [Vp8LTransforms.CrossColorPixel(32, 0, 32)];
        Vp8LTransforms.CrossColorForward(pixels, 1, 1, 2, tiles);
        Assert.Equal((0xff << 24) | (60 << 16) | (40 << 8) | ((90 - 100) & 0xff), pixels[0]);
    }

    [Fact]
    public void ColourIndexingBundlesIntoGreenLeastSignificantFirstWithAPartialLastBundle()
    {
        // Two colours, eleven pixels a row: one bit each, eight a bundle, so two bundles.
        int[] palette = [0x11111111, 0x22222222];
        int[] row = new int[11];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = palette[i % 3 == 0 ? 1 : 0];
        }

        int[] packed = Vp8LTransforms.IndexPixels(row, 11, 1, palette, Vp8LTransforms.PaletteBits(2));
        Assert.Equal(2, packed.Length);
        int expected0 = 0, expected1 = 0;
        for (int i = 0; i < 8; i++)
        {
            expected0 |= (i % 3 == 0 ? 1 : 0) << i;
        }

        for (int i = 8; i < 11; i++)
        {
            expected1 |= (i % 3 == 0 ? 1 : 0) << (i - 8);
        }

        Assert.Equal(unchecked((int)0xff000000) | (expected0 << 8), packed[0]);
        Assert.Equal(unchecked((int)0xff000000) | (expected1 << 8), packed[1]);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 0)]
    [InlineData(256, 0)]
    public void BundlingStepsAtTwoFourAndSixteenColours(int colours, int bits) =>
        Assert.Equal(bits, Vp8LTransforms.PaletteBits(colours));

    [Fact]
    public void TheDeltaCodedPaletteAddsBackToThePalette()
    {
        int[] palette = [unchecked((int)0xfffefdfc), 0x01020304, unchecked((int)0x80ffff00), 0x00000000];
        int[] deltas = Vp8LTransforms.DeltaCodePalette(palette);
        int[] sum = new int[deltas.Length];
        int previous = 0;
        for (int i = 0; i < deltas.Length; i++)
        {
            int d = deltas[i];
            previous = ((((d >> 24) + (previous >> 24)) & 0xff) << 24) | ((((d >> 16) + (previous >> 16)) & 0xff) << 16)
                | ((((d >> 8) + (previous >> 8)) & 0xff) << 8) | (((d & 0xff) + (previous & 0xff)) & 0xff);
            sum[i] = previous;
        }

        Assert.Equal(palette, sum);
    }

    [Fact]
    public void ThePaletteCollectorStopsAt256()
    {
        Assert.True(Vp8LTransforms.TryCollectPalette(WebpLosslessEncoderTransformImages.Colours(40, 40, 256, 3, true), out int[] c256));
        Assert.Equal(256, c256.Length);
        Assert.False(Vp8LTransforms.TryCollectPalette(WebpLosslessEncoderTransformImages.Colours(40, 40, 257, 3, true), out _));
    }

    [Fact]
    public void ModeAndMultiplierSearchesAreDeterministicAndPickAGoodModeOnAGradient()
    {
        int[] gradient = new int[64 * 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int v = (x + y) * 2;
                gradient[y * 64 + x] = unchecked((int)0xff000000) | (v << 16) | (v << 8) | v;
            }
        }

        int[] a = Vp8LTransforms.ChoosePredictorModes(gradient, 64, 64, 4, -1);
        int[] b = Vp8LTransforms.ChoosePredictorModes(gradient, 64, 64, 4, -1);
        Assert.Equal(a, b);

        // A pure plane is predicted exactly by L + T - TL (mode 12), by the select predictor
        // (mode 11) or the half-gradient clamp (13): never by black.
        foreach (int mode in a.Skip(5))
        {
            Assert.NotEqual(0, mode);
        }
    }
}
