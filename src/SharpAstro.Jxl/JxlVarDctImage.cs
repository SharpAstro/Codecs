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
/// <para>
/// <see cref="Encode"/> / <see cref="Decode"/> run that pipeline over the whole image at once. The
/// frame layer instead calls <see cref="QuantizeRegion"/> / <see cref="ReconstructRegion"/> once per
/// 256-px group through one reused <see cref="Workspace"/>, so its memory does not scale with
/// whole-image float planes; both forms run identical arithmetic per block.
/// </para>
/// </summary>
internal static class JxlVarDctImage
{
    private static readonly float[] MLf =
        { JxlQuantizer.MxLf, JxlQuantizer.MyLf, JxlQuantizer.MbLf };

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
        var settings = new Settings(globalScale, quantLf, hfMul, extraPrecision);

        // The whole image is one region: the same transform the frame encoder runs per 256-px group.
        var ws = new Workspace(width * height);
        for (int i = 0; i < width * height; i++)
            StoreXyb(ws.Coeff, i, srgb[0][i], srgb[1][i], srgb[2][i]);

        var lfQuant = new int[3][];
        var hfQuant = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            lfQuant[c] = new int[bw * bh];
            hfQuant[c] = new int[width * height];
        }

        QuantizeRegion(ws, bw, bh, settings, WholePlanes(lfQuant, bw), WholePlanes(hfQuant, width));

        return new Encoded
        {
            Width = width, Height = height, Bw = bw, Bh = bh,
            GlobalScale = globalScale, QuantLf = quantLf, HfMul = hfMul, ExtraPrecision = extraPrecision,
            LfQuant = lfQuant, HfQuant = hfQuant,
        };
    }

    public static float[][] Decode(Encoded e)
    {
        var settings = new Settings(e.GlobalScale, e.QuantLf, e.HfMul, e.ExtraPrecision);
        int w = e.Width, h = e.Height;

        var ws = new Workspace(w * h);
        ReconstructRegion(ws, e.Bw, e.Bh, settings, WholePlanes(e.LfQuant, e.Bw), WholePlanes(e.HfQuant, w));
        return ws.Coeff;
    }

    // ---- region pipeline ----
    //
    // Every step below is local to one 8×8 block: the DCT, quantization, chroma-from-luma (against the
    // same block's reconstructed Y), and the per-pixel XYB <-> sRGB conversion. With Gabor and EPF off
    // nothing reads across a block border, so a frame can run this one 256-px group at a time through a
    // reused Workspace instead of materializing whole-image planes, and produce identical bits.

    /// <summary>
    /// The per-frame constants of the transform: the quantizer, the default dequant matrices and the
    /// chroma-from-luma factors. Built once per frame rather than once per region.
    /// </summary>
    public sealed class Settings
    {
        public Settings(int globalScale, int quantLf, int hfMul, int extraPrecision)
        {
            Quant = new JxlQuantizer(globalScale, quantLf);
            Dq = JxlDequantMatrices.BuildDefault();
            HfMul = hfMul;
            ExtraPrecision = extraPrecision;
            (KxLf, KbLf) = JxlChromaFromLuma.LfFactors(
                JxlChromaFromLuma.DefaultFactorLf, JxlChromaFromLuma.DefaultFactorLf,
                JxlChromaFromLuma.DefaultColourFactor, JxlChromaFromLuma.DefaultBaseCorrelationX, JxlChromaFromLuma.DefaultBaseCorrelationB);
            (KxHf, KbHf) = JxlChromaFromLuma.HfFactors(
                0, 0, JxlChromaFromLuma.DefaultColourFactor, JxlChromaFromLuma.DefaultBaseCorrelationX, JxlChromaFromLuma.DefaultBaseCorrelationB);
        }

        public JxlQuantizer Quant { get; }
        public JxlDequantMatrices Dq { get; }
        public int HfMul { get; }
        public int ExtraPrecision { get; }
        public float KxLf { get; }
        public float KbLf { get; }
        public float KxHf { get; }
        public float KbHf { get; }
    }

    /// <summary>
    /// A window onto a plane of quantized samples: LF (one per block) or HF (one per pixel). Lets a region
    /// read and write either a whole-image plane or a region-sized one with the same code.
    /// </summary>
    public readonly record struct PlaneView(int[] Data, int Stride, int X0, int Y0)
    {
        public int Index(int x, int y) => (Y0 + y) * Stride + X0 + x;
    }

    /// <summary>
    /// Float scratch for one region: three coefficient planes and the reconstructed-luma plane, each laid
    /// out with the region's own width as the row stride. Reused across the groups of a frame.
    /// </summary>
    public sealed class Workspace
    {
        public Workspace(int maxPixels)
        {
            Coeff = [new float[maxPixels], new float[maxPixels], new float[maxPixels]];
            YRecon = new float[maxPixels];
        }

        /// <summary>Per channel [X, Y, B]: XYB samples in, coefficients during, sRGB out on decode.</summary>
        public float[][] Coeff { get; }

        public float[] YRecon { get; }
    }

    /// <summary>Views covering three whole-image planes of the given row stride.</summary>
    public static PlaneView[] WholePlanes(int[][] planes, int stride) =>
        [new(planes[0], stride, 0, 0), new(planes[1], stride, 0, 0), new(planes[2], stride, 0, 0)];

    /// <summary>Converts one sRGB sample triple (0..1) to XYB and stores it at <paramref name="i"/>.</summary>
    public static void StoreXyb(float[][] xyb, int i, float r, float g, float b)
    {
        float lr = JxlXyb.SrgbToLinear(r);
        float lg = JxlXyb.SrgbToLinear(g);
        float lb = JxlXyb.SrgbToLinear(b);
        (float x, float y, float bb) = JxlXyb.LinearToXyb(lr, lg, lb);
        xyb[0][i] = x; xyb[1][i] = y; xyb[2][i] = bb;
    }

    /// <summary>
    /// Forward-transforms and quantizes a region of <paramref name="bw"/>×<paramref name="bh"/> blocks
    /// whose XYB samples are already in <paramref name="ws"/>. The LF DC of each block goes through
    /// <paramref name="lf"/>, the HF AC through <paramref name="hf"/> (with 0 at the DC positions).
    /// </summary>
    public static void QuantizeRegion(Workspace ws, int bw, int bh, Settings s, PlaneView[] lf, PlaneView[] hf)
    {
        int width = bw * 8, height = bh * 8;
        for (int c = 0; c < 3; c++)
            ForwardDctBlocks(ws.Coeff[c], width, height);

        // The luma pass reads yRecon (times k = 0) before it writes it, so a reused buffer must start from
        // the zeros a fresh one would have: 0 * stale could otherwise turn a +0 into a -0.
        Array.Clear(ws.YRecon, 0, width * height);

        // Quantize luma (Y) first, keeping the reconstructed coefficients to decorrelate chroma against.
        QuantizeChannel(channel: 1, ws.Coeff[1], yRecon: ws.YRecon, kLf: 0f, kHf: 0f,
            width, bw, bh, s, lf[1], hf[1]);

        // X and B are decorrelated against reconstructed Y, then quantized.
        QuantizeChannel(channel: 0, ws.Coeff[0], yRecon: ws.YRecon, kLf: s.KxLf, kHf: s.KxHf,
            width, bw, bh, s, lf[0], hf[0]);
        QuantizeChannel(channel: 2, ws.Coeff[2], yRecon: ws.YRecon, kLf: s.KbLf, kHf: s.KbHf,
            width, bw, bh, s, lf[2], hf[2]);
    }

    /// <summary>
    /// Dequantizes, restores chroma, inverse-transforms and converts to sRGB a region of
    /// <paramref name="bw"/>×<paramref name="bh"/> blocks, leaving clamped sRGB (0..1) in
    /// <paramref name="ws"/>.<see cref="Workspace.Coeff"/> with row stride <c>bw·8</c>.
    /// </summary>
    public static void ReconstructRegion(Workspace ws, int bw, int bh, Settings s, PlaneView[] lf, PlaneView[] hf)
    {
        int w = bw * 8, h = bh * 8;
        float[][] coeff = ws.Coeff;
        for (int c = 0; c < 3; c++)
            DequantChannel(c, lf[c], hf[c], coeff[c], w, bw, bh, s);

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
                        coeff[0][gi] += (isDc ? s.KxLf : s.KxHf) * y;
                        coeff[2][gi] += (isDc ? s.KbLf : s.KbHf) * y;
                    }
            }

        for (int c = 0; c < 3; c++)
            InverseDctBlocks(coeff[c], w, h);

        XybToSrgbInPlace(coeff, w * h);
    }

    // ---- per-channel quantization (forward) ----

    private static void QuantizeChannel(
        int channel, float[] coeff, float[] yRecon, float kLf, float kHf,
        int width, int bw, int bh, Settings s, PlaneView lfQuant, PlaneView hfQuant)
    {
        bool isLuma = channel == 1;
        float lfScale = s.Quant.LfScale(MLf[channel], s.ExtraPrecision);
        float hfScale = s.Quant.HfScale(s.HfMul, QmScale[channel]);
        float[] weights = s.Dq.Get(channel, JxlVarDctTransform.Dct8);

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
                            lfQuant.Data[lfQuant.Index(bx, by)] = q;
                            // The DC position of the HF plane carries nothing; keep it 0 even when the
                            // plane is a reused region buffer.
                            hfQuant.Data[hfQuant.Index(baseX, baseY)] = 0;
                            if (isLuma)
                                yRecon[gi] = q * lfScale;
                        }
                        else
                        {
                            int wi = py * 8 + px;
                            int q = JxlQuantizer.QuantizeHf(value, weights[wi], hfScale);
                            hfQuant.Data[hfQuant.Index(baseX + px, baseY + py)] = q;
                            if (isLuma)
                                yRecon[gi] = JxlQuantizer.DequantizeHf(q, weights[wi], hfScale, channel);
                        }
                    }
            }
    }

    // ---- per-channel dequantization (inverse) ----

    private static void DequantChannel(
        int channel, PlaneView lfQuant, PlaneView hfQuant, float[] coeff,
        int width, int bw, int bh, Settings s)
    {
        float lfScale = s.Quant.LfScale(MLf[channel], s.ExtraPrecision);
        float hfScale = s.Quant.HfScale(s.HfMul, QmScale[channel]);
        float[] weights = s.Dq.Get(channel, JxlVarDctTransform.Dct8);

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int baseX = bx * 8, baseY = by * 8;
                coeff[baseY * width + baseX] = lfQuant.Data[lfQuant.Index(bx, by)] * lfScale; // DC
                for (int py = 0; py < 8; py++)
                    for (int px = 0; px < 8; px++)
                    {
                        if (px == 0 && py == 0)
                            continue;
                        int gi = (baseY + py) * width + baseX + px;
                        int wi = py * 8 + px;
                        coeff[gi] = JxlQuantizer.DequantizeHf(hfQuant.Data[hfQuant.Index(baseX + px, baseY + py)], weights[wi], hfScale, channel);
                    }
            }
    }

    // ---- colour + block transform helpers ----

    /// <summary>XYB → sRGB (clamped to 0..1) over the first <paramref name="count"/> samples, in place.</summary>
    private static void XybToSrgbInPlace(float[][] planes, int count)
    {
        for (int i = 0; i < count; i++)
        {
            (float r, float g, float b) = JxlXyb.XybToLinear(planes[0][i], planes[1][i], planes[2][i]);
            planes[0][i] = Math.Clamp(JxlXyb.LinearToSrgb(r), 0f, 1f);
            planes[1][i] = Math.Clamp(JxlXyb.LinearToSrgb(g), 0f, 1f);
            planes[2][i] = Math.Clamp(JxlXyb.LinearToSrgb(b), 0f, 1f);
        }
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
