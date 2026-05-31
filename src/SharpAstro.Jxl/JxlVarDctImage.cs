namespace SharpAstro.Jxl;

/// <summary>
/// The full-image VarDCT pixel pipeline for the minimal DCT8 encoder (ISO/IEC 18181-1 §K) — the
/// payload that the LfGroup / PassGroup bitstream sections carry. It composes the validated math
/// layers (<see cref="JxlXyb"/>, <see cref="JxlDct"/>, <see cref="JxlQuantizer"/>,
/// <see cref="JxlDequantMatrices"/>, <see cref="JxlChromaFromLuma"/>) into a whole-image transform:
///
/// <list type="bullet">
///   <item>sRGB → linear → XYB planes (channel order X, Y, B);</item>
///   <item>per 8×8 block, a forward DCT; the DC of each block forms the LF image (quantized with the
///         LF quantizer), the 63 AC coefficients form the HF data (quantized with the HF quantizer and
///         the default dequant matrix);</item>
///   <item>chroma-from-luma decorrelation of X and B against the <em>reconstructed</em> Y (so the
///         decoder, which only has reconstructed Y, restores them exactly), with the default factors
///         (kx = 0, kb = 1) applied separately to the LF DC and the HF AC.</item>
/// </list>
///
/// <para>
/// <see cref="Decode"/> mirrors the decode order (dequant LF → CfL-LF → dequant HF → CfL-HF → inverse
/// DCT → XYB→sRGB) so encode→decode round-trips to low RMSE (lossy). Width and height must be
/// multiples of 8 (the frame layer pads to that before calling). <c>qm_scale</c> is held at 1 per
/// channel for now (the minimal default); chroma subsampling and larger transforms are out of scope.
/// </para>
/// </summary>
internal static class JxlVarDctImage
{
    private static readonly float[] MLfUnscaled =
        { JxlQuantizer.MxLfUnscaled, JxlQuantizer.MyLfUnscaled, JxlQuantizer.MbLfUnscaled };

    // qm_scale per channel (X, Y, B). The minimal default leaves every channel at 1.
    private static readonly float[] QmScale = { 1f, 1f, 1f };

    /// <summary>The quantized VarDCT payload for one DCT8 image.</summary>
    public sealed class Encoded
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Bw { get; init; }
        public required int Bh { get; init; }
        public required int GlobalScale { get; init; }
        public required int QuantLf { get; init; }
        public required int HfMul { get; init; }
        public required int ExtraPrecision { get; init; }

        /// <summary>Quantized LF DC per channel [X, Y, B], one sample per block (<c>bw·bh</c>).</summary>
        public required int[][] LfQuant { get; init; }

        /// <summary>Quantized HF AC per channel [X, Y, B], full grid (<c>w·h</c>); DC positions are 0.</summary>
        public required int[][] HfQuant { get; init; }
    }

    public static Encoded Encode(float[][] srgb, int width, int height, int globalScale, int quantLf, int hfMul, int extraPrecision)
    {
        if (width % 8 != 0 || height % 8 != 0)
            throw new ArgumentException("JPEG XL VarDCT (DCT8): width and height must be multiples of 8.");
        int bw = width / 8, bh = height / 8;
        var quant = new JxlQuantizer(globalScale, quantLf);
        JxlDequantMatrices dq = JxlDequantMatrices.BuildDefault();

        float[][] coeff = ToXybPlanes(srgb, width, height);
        for (int c = 0; c < 3; c++)
            ForwardDctBlocks(coeff[c], width, height);

        (float kxLf, float kbLf) = JxlChromaFromLuma.LfFactors(
            JxlChromaFromLuma.DefaultFactorLf, JxlChromaFromLuma.DefaultFactorLf,
            JxlChromaFromLuma.DefaultColourFactor, JxlChromaFromLuma.DefaultBaseCorrelationX, JxlChromaFromLuma.DefaultBaseCorrelationB);
        (float kxHf, float kbHf) = JxlChromaFromLuma.HfFactors(
            0, 0, JxlChromaFromLuma.DefaultColourFactor, JxlChromaFromLuma.DefaultBaseCorrelationX, JxlChromaFromLuma.DefaultBaseCorrelationB);

        var lfQuant = new int[3][];
        var hfQuant = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            lfQuant[c] = new int[bw * bh];
            hfQuant[c] = new int[width * height];
        }

        // Quantize luma (Y) first, keeping the reconstructed coefficients to decorrelate chroma against.
        var yHat = new float[width * height];
        QuantizeChannel(channel: 1, coeff[1], yRecon: yHat, kLf: 0f, kHf: 0f,
            width, height, bw, bh, quant, dq, extraPrecision, hfMul, lfQuant[1], hfQuant[1]);

        // X and B are decorrelated against reconstructed Y, then quantized.
        QuantizeChannel(channel: 0, coeff[0], yRecon: yHat, kLf: kxLf, kHf: kxHf,
            width, height, bw, bh, quant, dq, extraPrecision, hfMul, lfQuant[0], hfQuant[0]);
        QuantizeChannel(channel: 2, coeff[2], yRecon: yHat, kLf: kbLf, kHf: kbHf,
            width, height, bw, bh, quant, dq, extraPrecision, hfMul, lfQuant[2], hfQuant[2]);

        return new Encoded
        {
            Width = width, Height = height, Bw = bw, Bh = bh,
            GlobalScale = globalScale, QuantLf = quantLf, HfMul = hfMul, ExtraPrecision = extraPrecision,
            LfQuant = lfQuant, HfQuant = hfQuant,
        };
    }

    public static float[][] Decode(Encoded e)
    {
        var quant = new JxlQuantizer(e.GlobalScale, e.QuantLf);
        JxlDequantMatrices dq = JxlDequantMatrices.BuildDefault();
        int w = e.Width, h = e.Height, bw = e.Bw, bh = e.Bh;

        (float kxLf, float kbLf) = JxlChromaFromLuma.LfFactors(
            JxlChromaFromLuma.DefaultFactorLf, JxlChromaFromLuma.DefaultFactorLf,
            JxlChromaFromLuma.DefaultColourFactor, JxlChromaFromLuma.DefaultBaseCorrelationX, JxlChromaFromLuma.DefaultBaseCorrelationB);
        (float kxHf, float kbHf) = JxlChromaFromLuma.HfFactors(
            0, 0, JxlChromaFromLuma.DefaultColourFactor, JxlChromaFromLuma.DefaultBaseCorrelationX, JxlChromaFromLuma.DefaultBaseCorrelationB);

        var coeff = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            coeff[c] = new float[w * h];
            DequantChannel(c, e.LfQuant[c], e.HfQuant[c], coeff[c], w, h, bw, bh, quant, dq, e.ExtraPrecision, e.HfMul);
        }

        // Chroma-from-luma restore: DC (LF factors) and AC (HF factors) use reconstructed Y.
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int baseX = bx * 8, baseY = by * 8;
                for (int py = 0; py < 8; py++)
                    for (int px = 0; px < 8; px++)
                    {
                        int gi = (baseY + py) * w + baseX + px;
                        float y = coeff[1][gi];
                        bool isDc = px == 0 && py == 0;
                        coeff[0][gi] += (isDc ? kxLf : kxHf) * y;
                        coeff[2][gi] += (isDc ? kbLf : kbHf) * y;
                    }
            }

        for (int c = 0; c < 3; c++)
            InverseDctBlocks(coeff[c], w, h);

        return FromXybPlanes(coeff, w, h);
    }

    // ---- per-channel quantization (forward) ----

    private static void QuantizeChannel(
        int channel, float[] coeff, float[] yRecon, float kLf, float kHf,
        int width, int height, int bw, int bh, JxlQuantizer quant, JxlDequantMatrices dq,
        int extraPrecision, int hfMul, int[] lfQuant, int[] hfQuant)
    {
        bool isLuma = channel == 1;
        float lfScale = quant.LfScale(MLfUnscaled[channel], extraPrecision);
        float hfScale = quant.HfScale(hfMul, QmScale[channel]);
        float[] weights = dq.Get(channel, JxlVarDctTransform.Dct8);

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int baseX = bx * 8, baseY = by * 8;
                for (int py = 0; py < 8; py++)
                    for (int px = 0; px < 8; px++)
                    {
                        int gi = (baseY + py) * width + baseX + px;
                        bool isDc = px == 0 && py == 0;
                        // Chroma is decorrelated against reconstructed luma (no-op for luma: k = 0).
                        float value = coeff[gi] - (isDc ? kLf : kHf) * yRecon[gi];

                        if (isDc)
                        {
                            int q = (int)MathF.Round(value / lfScale);
                            lfQuant[by * bw + bx] = q;
                            if (isLuma)
                                yRecon[gi] = q * lfScale;
                        }
                        else
                        {
                            int wi = py * 8 + px;
                            int q = JxlQuantizer.QuantizeHf(value, weights[wi], hfScale);
                            hfQuant[gi] = q;
                            if (isLuma)
                                yRecon[gi] = JxlQuantizer.DequantizeHf(q, weights[wi], hfScale, channel);
                        }
                    }
            }
    }

    // ---- per-channel dequantization (inverse) ----

    private static void DequantChannel(
        int channel, int[] lfQuant, int[] hfQuant, float[] coeff,
        int width, int height, int bw, int bh, JxlQuantizer quant, JxlDequantMatrices dq,
        int extraPrecision, int hfMul)
    {
        float lfScale = quant.LfScale(MLfUnscaled[channel], extraPrecision);
        float hfScale = quant.HfScale(hfMul, QmScale[channel]);
        float[] weights = dq.Get(channel, JxlVarDctTransform.Dct8);

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int baseX = bx * 8, baseY = by * 8;
                coeff[baseY * width + baseX] = lfQuant[by * bw + bx] * lfScale; // DC
                for (int py = 0; py < 8; py++)
                    for (int px = 0; px < 8; px++)
                    {
                        if (px == 0 && py == 0)
                            continue;
                        int gi = (baseY + py) * width + baseX + px;
                        int wi = py * 8 + px;
                        coeff[gi] = JxlQuantizer.DequantizeHf(hfQuant[gi], weights[wi], hfScale, channel);
                    }
            }
    }

    // ---- colour + block transform helpers ----

    private static float[][] ToXybPlanes(float[][] srgb, int width, int height)
    {
        var xyb = new float[3][];
        for (int c = 0; c < 3; c++)
            xyb[c] = new float[width * height];
        for (int i = 0; i < width * height; i++)
        {
            float lr = JxlXyb.SrgbToLinear(srgb[0][i]);
            float lg = JxlXyb.SrgbToLinear(srgb[1][i]);
            float lb = JxlXyb.SrgbToLinear(srgb[2][i]);
            (float x, float y, float b) = JxlXyb.LinearToXyb(lr, lg, lb);
            xyb[0][i] = x; xyb[1][i] = y; xyb[2][i] = b;
        }
        return xyb;
    }

    private static float[][] FromXybPlanes(float[][] xyb, int width, int height)
    {
        var srgb = new float[3][];
        for (int c = 0; c < 3; c++)
            srgb[c] = new float[width * height];
        for (int i = 0; i < width * height; i++)
        {
            (float r, float g, float b) = JxlXyb.XybToLinear(xyb[0][i], xyb[1][i], xyb[2][i]);
            srgb[0][i] = Math.Clamp(JxlXyb.LinearToSrgb(r), 0f, 1f);
            srgb[1][i] = Math.Clamp(JxlXyb.LinearToSrgb(g), 0f, 1f);
            srgb[2][i] = Math.Clamp(JxlXyb.LinearToSrgb(b), 0f, 1f);
        }
        return srgb;
    }

    private static void ForwardDctBlocks(float[] plane, int width, int height) => DctBlocks(plane, width, height, JxlDctDirection.Forward);
    private static void InverseDctBlocks(float[] plane, int width, int height) => DctBlocks(plane, width, height, JxlDctDirection.Inverse);

    private static void DctBlocks(float[] plane, int width, int height, JxlDctDirection dir)
    {
        var block = new float[64];
        for (int by = 0; by < height / 8; by++)
            for (int bx = 0; bx < width / 8; bx++)
            {
                int baseX = bx * 8, baseY = by * 8;
                for (int py = 0; py < 8; py++)
                    for (int px = 0; px < 8; px++)
                        block[py * 8 + px] = plane[(baseY + py) * width + baseX + px];

                JxlDct.Dct2d(block, 8, 8, dir);

                for (int py = 0; py < 8; py++)
                    for (int px = 0; px < 8; px++)
                        plane[(baseY + py) * width + baseX + px] = block[py * 8 + px];
            }
    }
}
