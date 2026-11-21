using System.Numerics;
using System.Runtime.InteropServices;

namespace SharpAstro.Jxl;

/// <summary>A decoded VarDCT frame: integer samples in the image's colour encoding and bit depth.</summary>
internal sealed class JxlVarDctResult
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int BitsPerSample { get; init; }

    /// <summary>One array per colour channel: three for RGB, one for grey.</summary>
    public required int[][] Channels { get; init; }
    public required JxlColorEncoding Color { get; init; }
}

/// <summary>
/// A general VarDCT frame decoder (ISO/IEC 18181-1 §F to §J), ported from jxl-oxide: jxl-frame for the sections
/// (<c>LfGlobal</c>, <c>LfGroup</c>, <c>HfGlobal</c>, <c>decode_pass_group</c>), jxl-vardct for their VarDCT parts
/// (<c>LfChannelDequantization</c>, <c>Quantizer</c>, <c>HfBlockContext</c>, <c>LfChannelCorrelation</c>,
/// <c>LfCoeff</c>, <c>HfMetadata</c>, <c>DequantMatrixSet</c>, <c>HfPass</c>, <c>write_hf_coeff</c>) and jxl-render
/// for the reconstruction (<c>render_vardct</c>: LF dequantisation, LF chroma-from-luma and adaptive smoothing, HF
/// dequantisation with the quant bias, HF chroma-from-luma, every DctSelect transform, then the Gabor-like and
/// edge-preserving filters) and the XYB conversion.
///
/// <para>
/// Memory: one integer plane per channel, the size of the frame rounded up to whole blocks, holds a group's HF
/// coefficients as they are decoded (every pass added in), then, reinterpreted as floats, that group's XYB pixels
/// after its inverse transforms, then the filtered image, and finally the integer output samples. Besides it only
/// the LF image and the per-block metadata (one entry per 8x8 block) span the frame. LF groups and PassGroups are
/// decoded in parallel, each from its own section of the codestream.
/// </para>
///
/// <para>
/// Refused with <see cref="NotSupportedException"/>, naming the feature: an LF frame, frame upsampling, YCbCr
/// (recompressed JPEG), a non-XYB VarDCT frame, extra channels, patches, splines, noise, float output, and output
/// encodings <see cref="JxlColorOutput"/> cannot produce.
/// </para>
/// </summary>
internal static class JxlVarDctDecoder
{
    private const int GroupDim = 256;      // VarDCT group edge in pixels
    private const int GroupBlocks = 32;    // in 8x8 blocks
    private const int LfGroupBlocks = 256; // LF-group edge in 8x8 blocks
    private const byte NoBlock = 255;      // dct-select marker: not the top-left block of a varblock

    public static JxlVarDctResult Decode(byte[] containerOrCodestream)
    {
        byte[] cs = JxlContainer.ExtractCodestream(containerOrCodestream);
        var br = new JxlBitReader(cs.AsSpan(2)); // skip FF 0A

        (int width, int height) = JxlSizeHeader.Read(ref br);
        JxlImageMetadata meta = JxlImageMetadata.Read(ref br);
        JxlFrameHeader frame = JxlFrameHeader.Read(ref br, meta, width, height);
        if (frame.Encoding != JxlFrameEncoding.VarDct)
            throw new NotSupportedException("JPEG XL: expected a VarDCT frame.");
        CheckSupported(meta, frame);
        JxlToc toc = JxlToc.Read(ref br, frame);

        var f = new Frame(meta, frame);

        if (toc.EntryCount == 1)
        {
            // One section: LfGlobal, LfGroup, HfGlobal and the PassGroup follow each other bit by bit.
            ReadLfGlobal(ref br, f);
            ReadLfGroup(ref br, f, 0);
            FinishLf(f);
            ReadHfGlobal(ref br, f);
            for (int pass = 0; pass < frame.NumPasses; pass++)
                DecodePassGroup(ref br, f, pass, 0);
            ReconstructGroup(f, 0);
        }
        else
        {
            int secStart = 2 + (int)br.BytesRead;
            int numLf = frame.NumLfGroups, numGroups = frame.NumGroups;

            var lfgBr = Section(cs, secStart, toc, 0);
            ReadLfGlobal(ref lfgBr, f);

            Parallel.For(0, numLf, l =>
            {
                var r = Section(cs, secStart, toc, 1 + l);
                ReadLfGroup(ref r, f, l);
            });
            FinishLf(f);

            var hfgBr = Section(cs, secStart, toc, 1 + numLf);
            ReadHfGlobal(ref hfgBr, f);

            Parallel.For(0, numGroups, g =>
            {
                for (int pass = 0; pass < frame.NumPasses; pass++)
                {
                    var r = Section(cs, secStart, toc, 2 + numLf + pass * numGroups + g);
                    DecodePassGroup(ref r, f, pass, g);
                }
                ReconstructGroup(f, g);
            });
        }

        if (frame.GaborWeights is { } gabor)
            JxlRestorationFilters.Gabor(f.Planes, f.Stride, f.Width, f.Height, gabor);
        if (frame.Epf is { } epf)
            JxlRestorationFilters.Epf(f.Planes, f.Stride, f.Width, f.Height, epf, f.EpfSigma, f.Bw);

        int[][] channels = JxlColorOutput.XybToInteger(
            f.Planes, f.Stride, f.Bh * 8, f.Width, f.Height, meta.Color, meta.IntensityTarget, meta.BitDepth.BitsPerSample);

        return new JxlVarDctResult
        {
            Width = f.Width,
            Height = f.Height,
            BitsPerSample = meta.BitDepth.BitsPerSample,
            Channels = channels,
            Color = meta.Color,
        };
    }

    private static void CheckSupported(in JxlImageMetadata meta, in JxlFrameHeader frame)
    {
        if (frame.UsesLfFrame)
            throw new NotSupportedException("JPEG XL VarDCT frames with an LF frame are not yet supported.");
        if (frame.Upsampling != 1)
            throw new NotSupportedException("JPEG XL frame upsampling is not yet supported.");
        if (frame.DoYcbcr || !meta.XybEncoded)
            throw new NotSupportedException("JPEG XL VarDCT without XYB (a recompressed JPEG, or RGB VarDCT) is not yet supported.");
        if (meta.NumExtraChannels != 0)
            throw new NotSupportedException("JPEG XL VarDCT with extra channels is not yet supported.");
        if (frame.HasPatches)
            throw new NotSupportedException("JPEG XL patches are not yet supported.");
        if (frame.HasSplines)
            throw new NotSupportedException("JPEG XL splines are not yet supported.");
        if (frame.HasNoise)
            throw new NotSupportedException("JPEG XL noise is not yet supported.");
        if (meta.BitDepth.FloatingPoint)
            throw new NotSupportedException("JPEG XL VarDCT with float samples is not yet supported.");
        JxlColorOutput.CheckSupported(meta.Color);
    }

    private static JxlBitReader Section(byte[] cs, int secStart, in JxlToc toc, int index)
    {
        long start = secStart + toc.Offsets[index], size = toc.Sizes[index];
        if (start + size > cs.Length)
            throw new InvalidDataException("JPEG XL frame section runs past the end of the codestream.");
        return new JxlBitReader(cs.AsSpan((int)start, (int)size));
    }

    /// <summary>Everything a frame decode shares across its sections.</summary>
    private sealed class Frame
    {
        public Frame(in JxlImageMetadata meta, in JxlFrameHeader header)
        {
            Meta = meta;
            Header = header;
            Width = header.ColorWidth;
            Height = header.ColorHeight;
            Bw = (Width + 7) / 8;
            Bh = (Height + 7) / 8;
            Stride = Bw * 8;
            GroupsPerRow = (Width + GroupDim - 1) / GroupDim;
            LfGroupsPerRow = (Width + GroupDim * 8 - 1) / (GroupDim * 8);
            CflW = (Width + 63) / 64;
            CflH = (Height + 63) / 64;

            long planeSize = (long)Stride * Bh * 8;
            if (planeSize > int.MaxValue)
                throw new NotSupportedException("JPEG XL VarDCT frame too large for this decoder.");
            Planes = [new int[planeSize], new int[planeSize], new int[planeSize]];
            int blocks = Bw * Bh;
            Lf = [new float[blocks], new float[blocks], new float[blocks]];
            LfQuant = [new int[blocks], new int[blocks], new int[blocks]];
            DctSelect = new byte[blocks];
            Array.Fill(DctSelect, NoBlock);
            HfMul = new int[blocks];
            XFromY = new int[CflW * CflH];
            BFromY = new int[CflW * CflH];
            EpfSigma = new float[blocks];
        }

        public JxlImageMetadata Meta { get; }
        public JxlFrameHeader Header { get; }
        public int Width { get; }
        public int Height { get; }
        public int Bw { get; }
        public int Bh { get; }
        public int Stride { get; }
        public int GroupsPerRow { get; }
        public int LfGroupsPerRow { get; }
        public int CflW { get; }
        public int CflH { get; }

        /// <summary>Per channel (X, Y, B): HF coefficients, then XYB samples (as float bits).</summary>
        public int[][] Planes { get; }

        /// <summary>Dequantised LF per block, physical order X, Y, B.</summary>
        public float[][] Lf { get; }

        /// <summary>Quantised LF per block, physical order X, Y, B (the HF block context reads it).</summary>
        public int[][] LfQuant { get; }

        public byte[] DctSelect { get; }
        public int[] HfMul { get; }
        public int[] XFromY { get; }
        public int[] BFromY { get; }
        public float[] EpfSigma { get; }

        // LfGlobal.
        public float MXLf = 1f / 32, MYLf = 1f / 4, MBLf = 1f / 2;
        public JxlLfGlobalVarDct? Vardct;
        public JxlMaConfig? GlobalTree;

        // HfGlobal.
        public JxlDequantMatrices? Dequant;
        public int NumHfPresets;
        public HfPass[] Passes = [];

        public JxlLfGlobalVarDct VarDct => Vardct ?? throw new InvalidOperationException("LfGlobal not read.");
    }

    // ---- LfGlobal (jxl-frame lf_global.rs) ----

    private static void ReadLfGlobal(ref JxlBitReader br, Frame f)
    {
        // Patches, splines and noise were refused from the frame flags, so none of them is present.
        if (!br.ReadBit()) // LfChannelDequantization all_default
        {
            f.MXLf = br.ReadF16();
            f.MYLf = br.ReadF16();
            f.MBLf = br.ReadF16();
            if (f.MXLf / 128 < 1e-8f || f.MYLf / 128 < 1e-8f || f.MBLf / 128 < 1e-8f)
                throw new InvalidDataException("JPEG XL LF dequantisation weight is too small.");
        }
        f.Vardct = JxlLfGlobalVarDct.Read(ref br);

        // GlobalModular: an optional global tree, then the global Modular image (the extra channels, of which a
        // supported VarDCT frame has none, so it reads nothing).
        if (br.ReadBit())
        {
            long nodeLimit = Math.Min(1 << 22, 1024 + (long)f.Width * f.Height * 3 / 16);
            f.GlobalTree = JxlMaConfig.Parse(ref br, (int)nodeLimit);
        }
    }

    // ---- LfGroup (jxl-frame lf_group.rs, jxl-vardct lf.rs and hf_metadata.rs) ----

    private static void ReadLfGroup(ref JxlBitReader br, Frame f, int l)
    {
        int numLf = f.Header.NumLfGroups;
        int lcol = l % f.LfGroupsPerRow, lrow = l / f.LfGroupsPerRow;
        int lfW = Math.Min(GroupDim * 8, f.Width - lcol * GroupDim * 8);
        int lfH = Math.Min(GroupDim * 8, f.Height - lrow * GroupDim * 8);
        int bw = (lfW + 7) / 8, bh = (lfH + 7) / 8;
        int bx0 = lcol * LfGroupBlocks, by0 = lrow * LfGroupBlocks;
        int bitDepth = f.Meta.BitDepth.BitsPerSample;

        // LfCoeff: extra_precision, then the quantised LF (Modular channels Y, X, B) at stream 1 + l.
        int extraPrecision = (int)br.ReadBits(2);
        JxlModularChannel[] lfq = JxlModularStream.Read(
            ref br, f.GlobalTree,
            [new JxlModularChannel(bw, bh), new JxlModularChannel(bw, bh), new JxlModularChannel(bw, bh)],
            (uint)(1 + l), bitDepth);

        JxlLfGlobalVarDct v = f.VarDct;
        float precisionScale = 1 << (9 - extraPrecision);
        double scaleInv = (double)v.GlobalScale * v.QuantLf;
        float[] scale =
        [
            (float)(f.MXLf * (double)precisionScale / scaleInv),
            (float)(f.MYLf * (double)precisionScale / scaleInv),
            (float)(f.MBLf * (double)precisionScale / scaleInv),
        ];
        int[] modularOf = [1, 0, 2]; // physical X, Y, B from Modular Y, X, B
        for (int c = 0; c < 3; c++)
        {
            int[] src = lfq[modularOf[c]].Data;
            float s = scale[c];
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    int q = src[y * bw + x];
                    int fi = (by0 + y) * f.Bw + bx0 + x;
                    f.LfQuant[c][fi] = q;
                    f.Lf[c][fi] = q * s;
                }
        }

        // HfMetadata at stream 1 + 2 * num_lf_groups + l.
        int nbBits = BitOperations.TrailingZeroCount(BitOperations.RoundUpToPowerOf2((uint)(bw * bh)));
        int nbBlocks = 1 + (int)br.ReadBits(nbBits);
        int cfW = (lfW + 63) / 64, cfH = (lfH + 63) / 64;
        JxlModularChannel[] hfm = JxlModularStream.Read(
            ref br, f.GlobalTree,
            [
                new JxlModularChannel(cfW, cfH), new JxlModularChannel(cfW, cfH),
                new JxlModularChannel(nbBlocks, 2), new JxlModularChannel(bw, bh),
            ],
            (uint)(1 + 2 * numLf + l), bitDepth);

        int cx0 = lcol * (LfGroupBlocks / 8), cy0 = lrow * (LfGroupBlocks / 8);
        for (int y = 0; y < cfH; y++)
            for (int x = 0; x < cfW; x++)
            {
                f.XFromY[(cy0 + y) * f.CflW + cx0 + x] = hfm[0].Data[y * cfW + x];
                f.BFromY[(cy0 + y) * f.CflW + cx0 + x] = hfm[1].Data[y * cfW + x];
            }

        PlaceBlocks(f, hfm[2].Data, nbBlocks, hfm[3].Data, bw, bh, bx0, by0);
    }

    /// <summary>
    /// Places the varblocks of one LF group in raster order of their top-left blocks (jxl-oxide
    /// <c>HfMetadata::parse</c>), and fills the EPF sigma of every block they cover.
    /// </summary>
    private static void PlaceBlocks(Frame f, int[] blockInfoRaw, int nbBlocks, int[] sharpness, int bw, int bh, int bx0, int by0)
    {
        JxlEpfParams? epf = f.Header.Epf;
        float epfQuantMul = epf is null ? 0f : epf.QuantMul * 65536f / f.VarDct.GlobalScale;
        var occupied = new bool[bw * bh];
        int dataIdx = 0;
        for (int y = 0; y < bh; y++)
        {
            int x = 0;
            while (x < bw)
            {
                if (occupied[y * bw + x])
                {
                    x++;
                    continue;
                }
                if (dataIdx >= nbBlocks)
                    throw new InvalidDataException("JPEG XL BlockInfo does not fill its LF group.");
                int dctSelectRaw = blockInfoRaw[dataIdx];
                if ((uint)dctSelectRaw > (uint)JxlVarDctTransform.Dct128x256)
                    throw new InvalidDataException($"JPEG XL invalid DctSelect {dctSelectRaw}.");
                var t = (JxlVarDctTransform)dctSelectRaw;
                int hfMul = blockInfoRaw[nbBlocks + dataIdx] + 1;
                if (hfMul <= 0)
                    throw new InvalidDataException("JPEG XL non-positive HfMul.");
                (int dw, int dh) = t.DctSelectSize();
                if (x % GroupBlocks + dw > GroupBlocks || y % GroupBlocks + dh > GroupBlocks)
                    throw new InvalidDataException("JPEG XL varblock crosses a PassGroup border.");
                if (x + dw > bw || y + dh > bh)
                    throw new InvalidDataException("JPEG XL varblock does not fit in its LF group.");

                float sigma = epf is null ? 0f : epfQuantMul / hfMul;
                for (int dy = 0; dy < dh; dy++)
                    for (int dx = 0; dx < dw; dx++)
                    {
                        int li = (y + dy) * bw + x + dx;
                        if (occupied[li])
                            throw new InvalidDataException("JPEG XL varblocks overlap.");
                        occupied[li] = true;
                        if (epf is not null)
                        {
                            int sh = sharpness[li];
                            if ((uint)sh >= 8)
                                throw new InvalidDataException("JPEG XL invalid EPF sharpness value.");
                            f.EpfSigma[(by0 + y + dy) * f.Bw + bx0 + x + dx] = sigma * epf.SharpLut[sh];
                        }
                    }
                int fi = (by0 + y) * f.Bw + bx0 + x;
                f.DctSelect[fi] = (byte)t;
                f.HfMul[fi] = hfMul;
                dataIdx++;
                x += dw;
            }
        }
    }

    /// <summary>LF chroma-from-luma, then adaptive LF smoothing (jxl-oxide <c>render_vardct</c>).</summary>
    private static void FinishLf(Frame f)
    {
        JxlLfGlobalVarDct v = f.VarDct;
        float kx = v.BaseCorrelationX + (v.XFactorLf - 128) / (float)v.ColourFactor;
        float kb = v.BaseCorrelationB + (v.BFactorLf - 128) / (float)v.ColourFactor;
        float[] lx = f.Lf[0], ly = f.Lf[1], lb = f.Lf[2];
        for (int i = 0; i < lx.Length; i++)
        {
            float y = ly[i];
            lx[i] += kx * y;
            lb[i] += kb * y;
        }

        if (!f.Header.SkipAdaptiveLfSmoothing)
        {
            double scaleInv = (double)v.GlobalScale * v.QuantLf;
            float[] lfScale =
            [
                (float)(512.0 * f.MXLf / scaleInv),
                (float)(512.0 * f.MYLf / scaleInv),
                (float)(512.0 * f.MBLf / scaleInv),
            ];
            JxlRestorationFilters.AdaptiveLfSmoothing(f.Lf, f.Bw, f.Bh, lfScale);
        }
    }

    // ---- HfGlobal (jxl-frame hf_global.rs, jxl-vardct hf_pass.rs) ----

    /// <summary>One pass's coefficient orders and HF distributions.</summary>
    private sealed class HfPass
    {
        /// <summary>[order id][channel]: packed (x | y &lt;&lt; 16) coefficient positions, or null for the natural order.</summary>
        public required int[]?[][] Orders { get; init; }
        public required JxlEntropyDecoder Dist { get; init; }

        public int[] Order(int orderId, int channel) => Orders[orderId][channel] ?? NaturalOrder(orderId);
    }

    private static readonly (int W, int H)[] OrderBlockSizes =
    [
        (8, 8), (8, 8), (16, 16), (32, 32), (16, 8), (32, 8), (32, 16),
        (64, 64), (64, 32), (128, 128), (128, 64), (256, 256), (256, 128),
    ];

    private static readonly int[]?[] NaturalOrders = new int[]?[13];

    /// <summary>The natural coefficient order of an order id (jxl-oxide <c>fill_natural_order</c>).</summary>
    private static int[] NaturalOrder(int orderId)
    {
        int[]? cached = Volatile.Read(ref NaturalOrders[orderId]);
        if (cached is not null)
            return cached;

        (int bw, int bh) = OrderBlockSizes[orderId];
        int yScale = bw / bh;
        var output = new int[bw * bh];
        int idx = 0;
        int lbw = bw / 8, lbh = bh / 8;
        while (idx < lbw * lbh)
        {
            output[idx] = (idx % lbw) | ((idx / lbw) << 16);
            idx++;
        }
        for (int dist = 1; dist < 2 * bw; dist++)
        {
            int margin = Math.Max(dist - bw, 0);
            for (int order = margin; order < dist - margin; order++)
            {
                int x, y;
                if (dist % 2 == 1)
                {
                    x = order;
                    y = dist - 1 - order;
                }
                else
                {
                    x = dist - 1 - order;
                    y = order;
                }
                if (x < lbw && y < lbw)
                    continue;
                if (y % yScale != 0)
                    continue;
                output[idx++] = x | ((y / yScale) << 16);
            }
        }
        Interlocked.CompareExchange(ref NaturalOrders[orderId], output, null);
        return NaturalOrders[orderId] ?? output;
    }

    private static void ReadHfGlobal(ref JxlBitReader br, Frame f)
    {
        JxlFrameHeader h = f.Header;
        f.Dequant = JxlDequantMatrices.Read(ref br, f.GlobalTree, h.NumLfGroups, f.Meta.BitDepth.BitsPerSample);

        int presetBits = BitOperations.TrailingZeroCount(BitOperations.RoundUpToPowerOf2((uint)h.NumGroups));
        f.NumHfPresets = (int)br.ReadBits(presetBits) + 1;

        JxlLfGlobalVarDct v = f.VarDct;
        var passes = new HfPass[h.NumPasses];
        for (int p = 0; p < passes.Length; p++)
        {
            uint usedOrders = br.ReadU32((0x5f, 0), (0x13, 0), (0, 0), (0, 13));
            var orders = new int[]?[13][];
            for (int i = 0; i < 13; i++)
                orders[i] = new int[]?[3];
            if (usedOrders != 0)
            {
                JxlEntropyDecoder decoder = JxlEntropyDecoder.Parse(ref br, 8);
                for (int orderId = 0; orderId < 13; orderId++)
                {
                    if ((usedOrders & 1) != 0)
                    {
                        (int bw, int bh) = OrderBlockSizes[orderId];
                        uint size = (uint)(bw * bh);
                        int[] nat = NaturalOrder(orderId);
                        for (int c = 0; c < 3; c++)
                        {
                            int[] perm = JxlEntropyDecoder.ReadPermutation(ref br, decoder, size, size / 64);
                            var order = new int[size];
                            for (int k = 0; k < order.Length; k++)
                                order[k] = nat[perm[k]];
                            orders[orderId][c] = order;
                        }
                    }
                    usedOrders >>= 1;
                }
                decoder.Finish();
            }

            JxlEntropyDecoder dist = JxlEntropyDecoder.Parse(ref br, (uint)(495 * f.NumHfPresets * v.NumBlockClusters));
            passes[p] = new HfPass { Orders = orders, Dist = dist };
        }
        f.Passes = passes;
    }

    // ---- PassGroup HF coefficients (jxl-vardct hf_coeff.rs write_hf_coeff) ----

    private static readonly int[] CoeffFreqContext =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19,
        20, 20, 21, 21, 22, 22, 23, 23, 23, 23, 24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26, 27,
        27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
    ];

    private static readonly int[] CoeffNumNonzeroContext =
    [
        0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123, 152, 152, 152, 152, 152, 152, 152, 152,
        180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206,
    ];

    private static void DecodePassGroup(ref JxlBitReader br, Frame f, int passIdx, int groupIdx)
    {
        HfPass pass = f.Passes[passIdx];
        int coeffShift = passIdx < f.Header.PassShifts.Length ? f.Header.PassShifts[passIdx] : 0;
        JxlLfGlobalVarDct v = f.VarDct;
        int numBlockClusters = v.NumBlockClusters;
        byte[] blockCtxMap = v.BlockCtxMap;
        int[] qfThresholds = v.QfThresholds;
        int[][] lfThresholds = v.LfThresholds;
        int lfIdxMul = (lfThresholds[0].Length + 1) * (lfThresholds[1].Length + 1) * (lfThresholds[2].Length + 1);
        int hfIdxMul = qfThresholds.Length + 1;

        int hfpBits = BitOperations.TrailingZeroCount(BitOperations.RoundUpToPowerOf2((uint)f.NumHfPresets));
        int hfp = (int)br.ReadBits(hfpBits);
        if (hfp >= f.NumHfPresets)
            throw new InvalidDataException("JPEG XL HF preset out of range.");

        JxlEntropyDecoder dist = pass.Dist.Clone();
        int ctxSize = 495 * numBlockClusters;
        ReadOnlySpan<byte> clusterMap = dist.ClusterMap.AsSpan(ctxSize * hfp, ctxSize);
        dist.Begin(ref br);

        int gx = groupIdx % f.GroupsPerRow, gy = groupIdx / f.GroupsPerRow;
        int bx0 = gx * GroupBlocks, by0 = gy * GroupBlocks;
        int bwG = Math.Min(GroupBlocks, f.Bw - bx0), bhG = Math.Min(GroupBlocks, f.Bh - by0);
        int stride = f.Stride;

        Span<int> nonZerosRow = stackalloc int[3 * GroupBlocks];
        nonZerosRow.Clear();
        int[] channelOf = [1, 0, 2]; // decode order Y, X, B

        for (int y = 0; y < bhG; y++)
        {
            for (int x = 0; x < bwG; x++)
            {
                int fi = (by0 + y) * f.Bw + bx0 + x;
                byte sel = f.DctSelect[fi];
                if (sel == NoBlock)
                    continue;
                var t = (JxlVarDctTransform)sel;
                int qf = f.HfMul[fi];
                (int w8, int h8) = t.DctSelectSize();
                int numBlocks = w8 * h8;
                int numBlocksLog = BitOperations.TrailingZeroCount(numBlocks);
                int orderId = t.OrderId();
                bool transpose = t.NeedTranspose();

                int lfIdx = 0;
                foreach (int c in (ReadOnlySpan<int>)[0, 2, 1])
                {
                    int[] thr = lfThresholds[c];
                    lfIdx *= thr.Length + 1;
                    int q = f.LfQuant[c][fi];
                    foreach (int threshold in thr)
                        if (q > threshold)
                            lfIdx++;
                }

                int hfIdx = 0;
                foreach (int threshold in qfThresholds)
                    if (qf > threshold)
                        hfIdx++;

                for (int ci = 0; ci < 3; ci++)
                {
                    int chIdx = ci * 13 + orderId;
                    int c = channelOf[ci];
                    Span<int> nz = nonZerosRow.Slice(c * GroupBlocks, GroupBlocks);

                    int idx = (chIdx * hfIdxMul + hfIdx) * lfIdxMul + lfIdx;
                    int blockCtx = blockCtxMap[idx];
                    int predicted = y == 0
                        ? (x == 0 ? 32 : nz[x - 1])
                        : (x == 0 ? nz[x] : (nz[x] + nz[x - 1] + 1) >> 1);
                    int nzIdx = predicted >= 8 ? 4 + predicted / 2 : predicted;
                    int nonZerosCtx = blockCtx + nzIdx * numBlockClusters;

                    int nonZeros = (int)dist.ReadVarintClustered(ref br, clusterMap[nonZerosCtx], 0);
                    if (nonZeros > (63 << numBlocksLog))
                        throw new InvalidDataException("JPEG XL varblock non-zero count too large.");

                    int nonZerosVal = (nonZeros + numBlocks - 1) >> numBlocksLog;
                    for (int dx = 0; dx < w8; dx++)
                        nz[x + dx] = nonZerosVal;
                    if (nonZeros == 0)
                        continue;

                    int[] plane = f.Planes[c];
                    int baseIdx = (by0 + y) * 8 * stride + (bx0 + x) * 8;
                    int isPrevNonZero = nonZeros <= numBlocks * 4 ? 1 : 0;
                    int[] order = pass.Order(orderId, c);
                    int coeffCtxBase = blockCtx * 458 + 37 * numBlockClusters;
                    ReadOnlySpan<byte> coeffClusters = clusterMap.Slice(coeffCtxBase, Math.Min(458, clusterMap.Length - coeffCtxBase));
                    for (int k = numBlocks; k < order.Length; k++)
                    {
                        int coeffCtx = (CoeffNumNonzeroContext[(nonZeros - 1) >> numBlocksLog]
                            + CoeffFreqContext[(k - numBlocks) >> numBlocksLog]) * 2 + isPrevNonZero;
                        if (coeffCtx >= coeffClusters.Length)
                            throw new InvalidDataException("JPEG XL too many zeros in a varblock's HF coefficients.");
                        uint ucoeff = dist.ReadVarintClustered(ref br, coeffClusters[coeffCtx], 0);
                        if (ucoeff == 0)
                        {
                            isPrevNonZero = 0;
                            continue;
                        }
                        int coeff = JxlModular.UnpackSigned(ucoeff) << coeffShift;
                        int packed = order[k];
                        int dxc = packed & 0xffff, dyc = packed >> 16;
                        if (transpose)
                            (dxc, dyc) = (dyc, dxc);
                        plane[baseIdx + dyc * stride + dxc] += coeff;
                        isPrevNonZero = 1;
                        nonZeros--;
                        if (nonZeros == 0)
                            break;
                    }
                }
            }
        }

        dist.Finish();
    }

    // ---- Reconstruction of one group (jxl-render vardct/mod.rs) ----

    private static void ReconstructGroup(Frame f, int groupIdx)
    {
        int gx = groupIdx % f.GroupsPerRow, gy = groupIdx / f.GroupsPerRow;
        int bx0 = gx * GroupBlocks, by0 = gy * GroupBlocks;
        int bwG = Math.Min(GroupBlocks, f.Bw - bx0), bhG = Math.Min(GroupBlocks, f.Bh - by0);
        int stride = f.Stride;
        JxlLfGlobalVarDct v = f.VarDct;
        JxlDequantMatrices dequant = f.Dequant ?? throw new InvalidOperationException("HfGlobal not read.");
        JxlFrameHeader h = f.Header;

        // HF dequantisation with the quant bias (dequant_hf_varblock_grouped), on the integer coefficients.
        float[] qmScale = [MathF.Pow(0.8f, h.XQmScale - 2), 1f, MathF.Pow(0.8f, h.BQmScale - 2)];
        float[] quantBias = [1f - 0.05465007330715401f, 1f - 0.07005449891748593f, 1f - 0.049935103337343655f];
        const float QuantBiasNumerator = 0.145f;

        for (int c = 0; c < 3; c++)
        {
            int[] plane = f.Planes[c];
            Span<float> fp = MemoryMarshal.Cast<int, float>(plane.AsSpan());
            float bias = quantBias[c];
            for (int y = 0; y < bhG; y++)
                for (int x = 0; x < bwG; x++)
                {
                    int fi = (by0 + y) * f.Bw + bx0 + x;
                    byte sel = f.DctSelect[fi];
                    if (sel == NoBlock)
                        continue;
                    var t = (JxlVarDctTransform)sel;
                    (int bw, int bh) = t.DctSelectSize();
                    int width = bw * 8, height = bh * 8;
                    float mul = 65536f / (v.GlobalScale * (float)f.HfMul[fi]) * qmScale[c];
                    float[] matrix = t.NeedTranspose() ? dequant.GetTransposed(c, t) : dequant.Get(c, t);
                    int baseIdx = (by0 + y) * 8 * stride + (bx0 + x) * 8;
                    for (int yy = 0; yy < height; yy++)
                    {
                        int row = baseIdx + yy * stride;
                        int mrow = yy * width;
                        for (int xx = 0; xx < width; xx++)
                        {
                            float q = plane[row + xx];
                            if (MathF.Abs(q) <= 1f)
                                q *= bias;
                            else
                                q -= QuantBiasNumerator / q;
                            q *= matrix[mrow + xx];
                            q *= mul;
                            fp[row + xx] = q;
                        }
                    }
                }
        }

        // HF chroma-from-luma over the whole group (chroma_from_luma_hf_grouped).
        {
            Span<float> px = MemoryMarshal.Cast<int, float>(f.Planes[0].AsSpan());
            Span<float> py = MemoryMarshal.Cast<int, float>(f.Planes[1].AsSpan());
            Span<float> pb = MemoryMarshal.Cast<int, float>(f.Planes[2].AsSpan());
            int gw = bwG * 8, gh = bhG * 8;
            int x0 = bx0 * 8, y0 = by0 * 8;
            float colourFactor = v.ColourFactor;
            for (int y = 0; y < gh; y++)
            {
                int cflRow = ((y0 + y) / 64) * f.CflW;
                int row = (y0 + y) * stride + x0;
                for (int x64 = 0; x64 * 64 < gw; x64++)
                {
                    int ti = cflRow + (x0 / 64) + x64;
                    float kx = v.BaseCorrelationX + f.XFromY[ti] / colourFactor;
                    float kb = v.BaseCorrelationB + f.BFromY[ti] / colourFactor;
                    int end = Math.Min(64, gw - x64 * 64);
                    for (int dx = 0; dx < end; dx++)
                    {
                        int i = row + x64 * 64 + dx;
                        float cy = py[i];
                        px[i] += kx * cy;
                        pb[i] += kb * cy;
                    }
                }
            }
        }

        // LF into the low frequencies, then every varblock's inverse transform (transform_with_lf_grouped).
        for (int c = 0; c < 3; c++)
        {
            Span<float> fp = MemoryMarshal.Cast<int, float>(f.Planes[c].AsSpan());
            float[] lf = f.Lf[c];
            for (int y = 0; y < bhG; y++)
                for (int x = 0; x < bwG; x++)
                {
                    int fi = (by0 + y) * f.Bw + bx0 + x;
                    byte sel = f.DctSelect[fi];
                    if (sel == NoBlock)
                        continue;
                    int baseIdx = (by0 + y) * 8 * stride + (bx0 + x) * 8;
                    JxlVarDctInverse.TransformVarblock(
                        fp, baseIdx, stride, (JxlVarDctTransform)sel, lf, f.Bw, bx0 + x, by0 + y);
                }
        }
    }
}
