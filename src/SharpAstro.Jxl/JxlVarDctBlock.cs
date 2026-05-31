namespace SharpAstro.Jxl;

/// <summary>
/// Per-varblock transform for the DCT-family DctSelect types (ISO/IEC 18181-1 §K.3), ported from
/// jxl-oxide's <c>transform_varblocks_inner</c> + the <c>transform_dct</c> branch. A varblock of type
/// <c>DctNxM</c> covers <c>bw×bh</c> 8×8 blocks; its coefficients live in an <c>8·bw × 8·bh</c> grid.
///
/// <para>
/// The low-frequency <c>bw×bh</c> DC values are stored separately (the LF image). On reconstruction
/// they are turned into the block's <b>LLF</b> coefficients by a forward DCT of size <c>bw×bh</c>
/// divided by the <c>scale_f</c> table, after which the full <c>8·bw × 8·bh</c> inverse DCT yields
/// pixels. The forward direction is the exact inverse: a full forward DCT, then the top-left LLF is
/// multiplied by <c>scale_f</c> and inverse-DCT'd back down to the LF DC values.
/// </para>
///
/// <para>
/// The non-DCT special transforms (DCT2 / DCT4 / Hornuss / DCT4x8 / AFV) are decode-only in the
/// reference and are added once the bitstream can validate them end-to-end against libjxl.
/// </para>
/// </summary>
internal static class JxlVarDctBlock
{
    // scale_f(c, logb) = SCALE_F[c << logb]; precomputed for c = 0…31 at b = 256 (dct_common.rs).
    private static readonly float[] ScaleFTable =
    {
        1.0000000000000000f, 0.9996047255830407f, 0.9984194528776054f, 0.9964458326264695f,
        0.9936866130906366f, 0.9901456355893141f, 0.9858278282666936f, 0.9807391980963174f,
        0.9748868211368796f, 0.9682788310563117f, 0.9609244059440204f, 0.9528337534340876f,
        0.9440180941651672f, 0.9344896436056892f, 0.9242615922757944f, 0.9133480844001980f,
        0.9017641950288744f, 0.8895259056651056f, 0.8766500784429904f, 0.8631544288990163f,
        0.8490574973847023f, 0.8343786191696513f, 0.8191378932865928f, 0.8033561501721485f,
        0.7870549181591013f, 0.7702563888779096f, 0.7529833816270532f, 0.7352593067735488f,
        0.7171081282466044f, 0.6985543251889097f, 0.6796228528314652f, 0.6603391026591464f,
    };

    private static float ScaleF(int c, int logb) => ScaleFTable[c << logb];

    private static int Log2Pow2(int n) => System.Numerics.BitOperations.TrailingZeroCount((uint)n);

    /// <summary>
    /// Reconstruct pixels from a DCT-family coefficient block (in place). On entry the top-left
    /// <paramref name="bw"/>×<paramref name="bh"/> region holds the LF DC values; the rest holds HF
    /// coefficients. On exit <paramref name="block"/> holds the <c>8·bw × 8·bh</c> pixels.
    /// </summary>
    public static void InverseTransform(float[] block, int bw, int bh)
    {
        int width = bw * 8;
        int n = bw * bh;
        if (n > 1)
        {
            int logbw = Log2Pow2(bw), logbh = Log2Pow2(bh);
            var llf = new float[n];
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    llf[y * bw + x] = block[y * width + x];

            JxlDct.Dct2d(llf, bw, bh, JxlDctDirection.Forward);

            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    block[y * width + x] = llf[y * bw + x] / (ScaleF(y, 5 - logbh) * ScaleF(x, 5 - logbw));
        }

        JxlDct.Dct2d(block, width, bh * 8, JxlDctDirection.Inverse);
    }

    /// <summary>
    /// Analyse a pixel block (in place) into DCT-family coefficients and return the LF DC values.
    /// On exit <paramref name="block"/> holds the full <c>8·bw × 8·bh</c> coefficients (top-left =
    /// LLF); the returned array holds the <paramref name="bw"/>×<paramref name="bh"/> LF DC values.
    /// </summary>
    public static float[] ForwardTransform(float[] block, int bw, int bh)
    {
        int width = bw * 8;
        int n = bw * bh;

        JxlDct.Dct2d(block, width, bh * 8, JxlDctDirection.Forward);

        var lf = new float[n];
        if (n > 1)
        {
            int logbw = Log2Pow2(bw), logbh = Log2Pow2(bh);
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    lf[y * bw + x] = block[y * width + x] * (ScaleF(y, 5 - logbh) * ScaleF(x, 5 - logbw));

            JxlDct.Dct2d(lf, bw, bh, JxlDctDirection.Inverse);
        }
        else
        {
            lf[0] = block[0];
        }
        return lf;
    }
}
