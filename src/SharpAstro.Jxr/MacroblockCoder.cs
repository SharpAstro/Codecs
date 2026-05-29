using System.Numerics;

namespace SharpAstro.Jxr;

/// <summary>
/// The per-macroblock band entropy coder for <b>YUV444</b>, ported from jxrlib's
/// <c>EncodeMacroblockDC/Lowpass/Highpass</c> + <c>CodeCBP</c>/<c>CodeCoeffs</c>
/// (segenc.c) and their decode mirrors <c>DecodeMacroblock*</c> + <c>DecodeCBP</c>/
/// <c>DecodeCoeffs</c> (segdec.c). It assembles the leaf coders proved in earlier
/// rungs — adaptive VLC (<see cref="VlcSymbolCodec"/>), run/level
/// (<see cref="CoefficientSyntax"/>), CBP prediction (<see cref="CbpPrediction"/>),
/// model bits (<see cref="ModelBits"/>), adaptive scan (<see cref="AdaptiveScan"/>) —
/// over the shared <see cref="CodingContext"/> pool into a complete macroblock.
///
/// <para>This is "Rung 6": our-encode ↔ our-decode of a single macroblock's
/// quantized/predicted residual coefficients (DC + LP + HP + CBP), no oracle. The
/// DC/LP bands serialize the <see cref="Macroblock.BlockDc"/> arrays directly; the HP
/// band serializes the <see cref="Macroblock.Plane"/> coefficients and bakes the HP
/// dequantizer into the decode (so a round-trip is bit-exact when <c>hpQp == 1</c>).
/// The DC/LP/HP bands are written to three separate streams (the SPATIAL multiplexing
/// into one codestream is a tiling concern for the next rung).</para>
/// </summary>
internal static class MacroblockCoder
{
    private const int CtDc = CodingContext.CtDc;            // 5
    private const int CtHp = CodingContext.CtDc + CodingContext.ContextX; // 13

    // ----------------------------------------------------------------- public API

    /// <summary>Encode <paramref name="mb"/> (YUV444) into the DC/LP/AC streams (single isolated MB, no neighbors).</summary>
    public static void Encode(CodingContext ctx, Macroblock mb,
                              BitWriter dc, BitWriter lp, BitWriter ac,
                              bool ctxLeft = true, bool ctxTop = true)
    {
        RequireYuv444(ctx);
        EncodeDc(ctx, mb, dc);
        EncodeLowpass(ctx, mb, lp);
        EncodeHighpass(ctx, mb, ac, ctxLeft, ctxTop, null, null);
    }

    /// <summary>Decode a YUV444 macroblock from the DC/LP/AC streams into <paramref name="mb"/> (single isolated MB).</summary>
    public static void Decode(CodingContext ctx, Macroblock mb,
                              ref BitReader dc, ref BitReader lp, ref BitReader ac,
                              int hpQp = 1, bool ctxLeft = true, bool ctxTop = true)
    {
        RequireYuv444(ctx);
        DecodeDc(ctx, mb, ref dc);
        DecodeLowpass(ctx, mb, ref lp);
        DecodeHighpass(ctx, mb, ref ac, hpQp, ctxLeft, ctxTop, null, null);
    }

    private static void RequireYuv444(CodingContext ctx)
    {
        if (ctx.ColorFormat != ColorFormat.Yuv444 || ctx.Channels != 3)
            throw new NotSupportedException("MacroblockCoder currently supports YUV444 (3 channels) only.");
    }

    // ===================================================================== DC band

    internal static void EncodeDc(CodingContext ctx, Macroblock mb, BitWriter w)
    {
        var model = ctx.ModelDc;
        var lapMean = new int[2];

        int dcY = mb.BlockDc[0][0], dcU = mb.BlockDc[1][0], dcV = mb.BlockDc[2][0];
        int qY = Math.Abs(dcY), qU = Math.Abs(dcU), qV = Math.Abs(dcV);

        int mbY = model.FlcBits[0];
        if (mbY != 0) qY >>= mbY;
        int mbC = model.FlcBits[1];
        if (mbC != 0) { qU >>= mbC; qV >>= mbC; }

        int index = (qY != 0 ? 4 : 0) + (qU != 0 ? 2 : 0) + (qV != 0 ? 1 : 0);
        ctx.AHexpt[2].Encode(w, index); // YUV 3D significance (alphabet 8, no discriminant)

        // luminance DC
        if (qY != 0) { CoefficientSyntax.EncodeAbsLevel(w, ctx.AHexpt[3], qY + 1); lapMean[0]++; }
        w.WriteBits((uint)Math.Abs(dcY), mbY);
        if (dcY != 0) w.WriteBit(dcY < 0);

        // chrominance DC (U then V share AHexpt[4])
        if (qU != 0) { CoefficientSyntax.EncodeAbsLevel(w, ctx.AHexpt[4], qU + 1); lapMean[1]++; }
        w.WriteBits((uint)Math.Abs(dcU), mbC);
        if (dcU != 0) w.WriteBit(dcU < 0);

        if (qV != 0) { CoefficientSyntax.EncodeAbsLevel(w, ctx.AHexpt[4], qV + 1); lapMean[1]++; }
        w.WriteBits((uint)Math.Abs(dcV), mbC);
        if (dcV != 0) w.WriteBit(dcV < 0);

        ModelBits.UpdateMb(ctx.ColorFormat, ctx.Channels, lapMean, model);
    }

    internal static void DecodeDc(CodingContext ctx, Macroblock mb, ref BitReader r)
    {
        var model = ctx.ModelDc;
        var lapMean = new int[2];
        for (var i = 0; i < ctx.Channels; i++) Array.Clear(mb.BlockDc[i]);

        int index = ctx.AHexpt[2].Decode(ref r);
        int qY = index >> 2, qU = (index >> 1) & 1, qV = index & 1;

        int mbY = model.FlcBits[0];
        if (qY != 0) { qY = CoefficientSyntax.DecodeAbsLevel(ref r, ctx.AHexpt[3]) - 1; lapMean[0]++; }
        if (mbY != 0) qY = (qY << mbY) | (int)r.ReadBits(mbY);
        if (qY != 0 && r.ReadBit()) qY = -qY;
        mb.BlockDc[0][0] = qY;

        int mbC = model.FlcBits[1];
        if (qU != 0) { qU = CoefficientSyntax.DecodeAbsLevel(ref r, ctx.AHexpt[4]) - 1; lapMean[1]++; }
        if (mbC != 0) qU = (qU << mbC) | (int)r.ReadBits(mbC);
        if (qU != 0 && r.ReadBit()) qU = -qU;
        mb.BlockDc[1][0] = qU;

        if (qV != 0) { qV = CoefficientSyntax.DecodeAbsLevel(ref r, ctx.AHexpt[4]) - 1; lapMean[1]++; }
        if (mbC != 0) qV = (qV << mbC) | (int)r.ReadBits(mbC);
        if (qV != 0 && r.ReadBit()) qV = -qV;
        mb.BlockDc[2][0] = qV;

        ModelBits.UpdateMb(ctx.ColorFormat, ctx.Channels, lapMean, model);
    }

    // ===================================================================== LP band

    internal static void EncodeLowpass(CodingContext ctx, Macroblock mb, BitWriter w)
    {
        var model = ctx.ModelLp;
        var scan = ctx.ScanLowpass;
        var lapMean = new int[2];
        var rl = new int[3][];
        var residual = new int[3][];
        var numCoeffs = new int[3];

        int mbits = model.FlcBits[0];
        for (var ch = 0; ch < 3; ch++)
        {
            rl[ch] = new int[32];
            residual[ch] = new int[16];
            numCoeffs[ch] = ScanLowpass(mb.BlockDc[ch], residual[ch], scan, mbits, rl[ch]);
            mbits = model.FlcBits[1];
        }

        // YUV444 lowpass CBP (3-bit channel pattern), with the adaptive "raw mode" model.
        const int max = 3 * 4 - 5; // 7
        int cbp = (numCoeffs[0] > 0 ? 1 : 0) + (numCoeffs[1] > 0 ? 2 : 0) + (numCoeffs[2] > 0 ? 4 : 0);
        int countM = ctx.CbpCountMax, countZ = ctx.CbpCountZero;
        if (countZ <= 0 || countM < 0)
        {
            int val = countM < countZ ? max - cbp : cbp;
            if (val == 0) w.WriteBits(0, 1);
            else if (val == 1) w.WriteBits((3 + 1) & 0x6, 3);
            else w.WriteBits((uint)(val + max + 1), 3 + 1);
        }
        else
        {
            w.WriteBits((uint)cbp, 3);
        }
        ctx.CbpCountMax = Clamp8(countM + 1 - 4 * (cbp == max ? 1 : 0));
        ctx.CbpCountZero = Clamp8(countZ + 1 - 4 * (cbp == 0 ? 1 : 0));

        mbits = model.FlcBits[0];
        for (var ch = 0; ch < 3; ch++)
        {
            if (numCoeffs[ch] != 0)
            {
                lapMean[ch == 0 ? 0 : 1] += numCoeffs[ch];
                EncodeBlock(ch > 0, rl[ch], numCoeffs[ch], ctx, CtDc, w, 1);
            }
            if (mbits != 0)
                for (var k = 1; k < 16; k++)
                    w.WriteBits((uint)(residual[ch][k] >> 1), mbits + (residual[ch][k] & 1));
            mbits = model.FlcBits[1];
        }

        ModelBits.UpdateMb(ctx.ColorFormat, ctx.Channels, lapMean, model);
    }

    internal static void DecodeLowpass(CodingContext ctx, Macroblock mb, ref BitReader r)
    {
        var model = ctx.ModelLp;
        var scan = ctx.ScanLowpass;
        var lapMean = new int[2];
        var rl = new int[32];

        // YUV444 lowpass CBP.
        int cbp;
        int countM = ctx.CbpCountMax, countZ = ctx.CbpCountZero;
        const int max = 3 * 4 - 5; // 7
        if (countZ <= 0 || countM < 0)
        {
            cbp = 0;
            if (r.ReadBit())
            {
                cbp = 1;
                int k = (int)r.ReadBits(3 - 1);
                if (k != 0) cbp = k * 2 + (int)r.ReadBits(1);
            }
            if (countM < countZ) cbp = max - cbp;
        }
        else
        {
            cbp = (int)r.ReadBits(3);
        }
        ctx.CbpCountMax = Clamp8(countM + 1 - 4 * (cbp == max ? 1 : 0));
        ctx.CbpCountZero = Clamp8(countZ + 1 - 4 * (cbp == 0 ? 1 : 0));

        int mbits = model.FlcBits[0];
        for (var ch = 0; ch < 3; ch++)
        {
            int[] coeffs = mb.BlockDc[ch];
            if ((cbp & 1) != 0)
            {
                int n = DecodeBlock(ch > 0, rl, ctx, CtDc, ref r, 1);
                lapMean[ch == 0 ? 0 : 1] += n;
                int idx = 1;
                for (var k = 0; k < n; k++)
                {
                    idx += rl[k * 2];
                    coeffs[scan.Scan[idx]] = rl[k * 2 + 1];
                    scan.Visit(idx);
                    idx++;
                }
            }

            if (mbits != 0)
            {
                int mask = (1 << mbits) - 1;
                for (var k = 1; k < 16; k++)
                {
                    if (coeffs[k] != 0)
                    {
                        int r1 = (int)BitOperations.RotateLeft((uint)coeffs[k], mbits);
                        coeffs[k] = (r1 ^ (int)r.ReadBits(mbits)) - (r1 & mask);
                    }
                    else
                    {
                        uint v = r.PeekBits(mbits + 1);
                        int val = (int)((v >> 1) ^ (uint)-(int)(v & 1)) + (int)(v & 1);
                        coeffs[k] = val;
                        r.SkipBits(mbits + (val != 0 ? 1 : 0));
                    }
                }
            }
            mbits = model.FlcBits[1];
            cbp >>= 1;
        }

        ModelBits.UpdateMb(ctx.ColorFormat, ctx.Channels, lapMean, model);
    }

    // ===================================================================== HP band

    internal static void EncodeHighpass(CodingContext ctx, Macroblock mb, BitWriter w,
                                        bool ctxLeft, bool ctxTop, int[]? leftCbp, int[]? topCbp)
    {
        CodeCbp(ctx, mb, w, ctxLeft, ctxTop, leftCbp, topCbp);
        CodeCoeffs(ctx, mb, w);
    }

    internal static void DecodeHighpass(CodingContext ctx, Macroblock mb, ref BitReader r, int hpQp,
                                        bool ctxLeft, bool ctxTop, int[]? leftCbp, int[]? topCbp)
    {
        DecodeCbp(ctx, mb, ref r);
        // predCBPDec reconstructs the actual CBP from the transmitted residual + neighbors.
        for (var i = 0; i < 3; i++)
            mb.Cbp[i] = CbpPrediction.PredictDec(mb.DiffCbp[i], ctxLeft, ctxTop,
                topCbp?[i] ?? 0, leftCbp?[i] ?? 0, i, ctx.Cbp);
        DecodeCoeffs(ctx, mb, ref r, hpQp);
    }

    // ----------------------------------------------------------- CBP (encode)

    private static readonly int[] CbpNumOnes = { 0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4 };
    private static readonly int[] CbpTabLen = { 0, 2, 2, 2, 2, 2, 3, 2, 2, 3, 3, 2, 3, 2, 2, 0 };
    private static readonly int[] CbpTabCode = { 0, 0, 1, 0, 2, 1, 4, 3, 3, 5, 6, 2, 7, 1, 0, 0 };
    private static readonly int[] CbpGTab0 = { 0, 1, 1, 2, 1, 3, 3, 4, 1, 3, 3, 4, 2, 4, 4, 5 };
    private static readonly int[] CbpGFl0 = { 0, 2, 2, 1, 2, 2, 2, 2, 2, 2, 2, 2, 1, 2, 2, 0 };
    private static readonly int[] CbpGCode0 = { 0, 0, 1, 0, 2, 0, 1, 0, 3, 2, 3, 1, 1, 2, 3, 0 };

    private static void CodeCbp(CodingContext ctx, Macroblock mb, BitWriter w,
                                bool ctxLeft, bool ctxTop, int[]? leftCbp, int[]? topCbp)
    {
        // predCBPEnc: derive the actual CBP from significant HP coefficients, then the
        // transmitted residual via the adaptive prediction model + neighbors.
        int acThreshold0 = (1 << ctx.ModelAc.FlcBits[0]) - 1;
        int acThreshold1 = (1 << ctx.ModelAc.FlcBits[1]) - 1;
        for (var ch = 0; ch < 3; ch++)
        {
            int threshold = ch == 0 ? acThreshold0 : acThreshold1, threshold2 = threshold * 2 + 1;
            int cbp = 0;
            for (var j = 0; j < 16; j++)
            {
                int off = MacroblockLayout.BlkOffset[j];
                for (var i = 1; i < 16; i++)
                    if ((uint)(mb.Plane[ch][off + i] + threshold) >= (uint)threshold2) { cbp |= 1 << j; break; }
            }
            mb.Cbp[ch] = cbp;
            mb.DiffCbp[ch] = CbpPrediction.PredictEnc(cbp, ctxLeft, ctxTop,
                topCbp?[ch] ?? 0, leftCbp?[ch] ?? 0, ch, ctx.Cbp);
        }

        int diffY = mb.DiffCbp[0], diffU = mb.DiffCbp[1], diffV = mb.DiffCbp[2];

        // super-block presence pattern (YUV444 ORs the chroma into luma).
        int pattern = 0, dy = diffY | (diffU | diffV);
        for (var iBlock = 0; iBlock < 4; iBlock++)
        {
            pattern |= (dy & 0xf) != 0 ? 0x10 : 0;
            dy >>= 4;
            pattern >>= 1;
        }

        int count = CbpNumOnes[pattern];
        ctx.CbpCy1.Encode(w, count);
        ctx.CbpCy1.Discriminant += ctx.CbpCy1.Delta(count);
        if (CbpTabLen[pattern] != 0) w.WriteBits((uint)CbpTabCode[pattern], CbpTabLen[pattern]);

        for (var iBlock = 0; iBlock < 4; iBlock++)
        {
            int code = diffY & 0xf;
            int codeU = diffU & 0xf, codeV = diffV & 0xf;
            code |= (codeU != 0 ? 1 : 0) << 4;
            code |= (codeV != 0 ? 1 : 0) << 5;
            diffY >>= 4; diffU >>= 4; diffV >>= 4;

            if (code != 0)
            {
                int chroma = code >> 4;
                code &= 0xf;
                int val = chroma != 0 ? (CbpGTab0[code] > 2 ? 8 : CbpGTab0[code] + 6 - 1) : CbpGTab0[code] - 1;
                ctx.CbpCy.Encode(w, val);
                ctx.CbpCy.Discriminant += ctx.CbpCy.Delta(val);

                if (chroma != 0)
                {
                    if (chroma == 1) w.WriteBits(1, 1);
                    else w.WriteBits((uint)(3 - chroma), 2);
                }
                if (val == 8)
                {
                    if (CbpGTab0[code] == 3) w.WriteBits(1, 1);
                    else w.WriteBits((uint)(5 - CbpGTab0[code]), 2);
                }
                if (CbpGFl0[code] != 0) w.WriteBits((uint)CbpGCode0[code], CbpGFl0[code]);

                // YUV444 per-block chroma sub-CBP (U then V) via AHexpt[1] (alphabet 4).
                int sub = codeU;
                for (var k = 0; k < 2; k++)
                {
                    if (sub != 0)
                    {
                        ctx.AHexpt[1].Encode(w, CbpNumOnes[sub] - 1);
                        if (CbpTabLen[sub] != 0) w.WriteBits((uint)CbpTabCode[sub], CbpTabLen[sub]);
                    }
                    sub = codeV;
                }
            }
        }
    }

    // ----------------------------------------------------------- CBP (decode)

    private static readonly int[] CbpGFlc0 = { 0, 2, 1, 2, 2, 0 };
    private static readonly int[] CbpGOff0 = { 0, 4, 2, 8, 12, 1 };
    private static readonly int[] CbpGOut0 = { 0, 15, 3, 12, 1, 2, 4, 8, 5, 6, 9, 10, 7, 11, 13, 14 };

    private static void DecodeCbp(CodingContext ctx, Macroblock mb, ref BitReader r)
    {
        int cbpY = 0, cbpU = 0, cbpV = 0;

        int numCbp = ctx.CbpCy1.Decode(ref r);
        ctx.CbpCy1.Discriminant += ctx.CbpCy1.Delta(numCbp);
        numCbp = ExpandCbpCount(numCbp, ref r);

        for (var iBlock = 0; iBlock < 4; iBlock++)
        {
            if ((numCbp & (1 << iBlock)) == 0) continue;

            int blockCbp = ctx.CbpCy.Decode(ref r);
            ctx.CbpCy.Discriminant += ctx.CbpCy.Delta(blockCbp);
            uint val = (uint)blockCbp + 1;
            blockCbp = 0;

            if (val >= 6) // chroma present
            {
                blockCbp = r.ReadBit() ? 0x10 : (r.ReadBit() ? 0x20 : 0x30);
                if (val == 9)
                {
                    if (r.ReadBit()) { /* val stays 9 */ }
                    else if (r.ReadBit()) val = 10;
                    else val = 11;
                }
                val -= 6;
            }
            int code1 = CbpGOff0[val];
            if (CbpGFlc0[val] != 0) code1 += (int)r.ReadBits(CbpGFlc0[val]);
            blockCbp += CbpGOut0[code1];

            cbpY |= (blockCbp & 0xf) << (iBlock * 4);
            for (var k = 0; k < 2; k++)
            {
                if (((blockCbp >> (k + 4)) & 1) == 0) continue;
                int code = ExpandChromaSubCbp(ctx.AHexpt[1].Decode(ref r), ref r);
                if (k == 0) cbpU |= code << (iBlock * 4);
                else cbpV |= code << (iBlock * 4);
            }
        }

        mb.DiffCbp[0] = cbpY;
        mb.DiffCbp[1] = cbpU;
        mb.DiffCbp[2] = cbpV;
    }

    // The CbpCy1 count symbol expands to the 4-bit super-block pattern (mirror of CodeCBP's pattern→count).
    private static int ExpandCbpCount(int numCbp, ref BitReader r)
    {
        switch (numCbp)
        {
            case 1: return 1 << (int)r.ReadBits(2);
            case 2:
                int t = (int)r.ReadBits(2);
                if (t == 0) return 3;
                if (t == 1) return 5;
                int[] tab = { 6, 9, 10, 12 };
                return tab[t * 2 + (r.ReadBit() ? 1 : 0) - 4];
            case 3: return 0xf ^ (1 << (int)r.ReadBits(2));
            case 4: return 0xf;
            default: return numCbp; // 0 -> no blocks
        }
    }

    // The AHexpt[1] sub-CBP symbol expands to a 4-bit chroma block pattern (same shape as ExpandCbpCount).
    private static int ExpandChromaSubCbp(int code, ref BitReader r)
    {
        switch (code)
        {
            case 0: return 1 << (int)r.ReadBits(2);
            case 1:
                int t = (int)r.ReadBits(2);
                if (t == 0) return 3;
                if (t == 1) return 5;
                int[] tab = { 6, 9, 10, 12 };
                return tab[t * 2 + (r.ReadBit() ? 1 : 0) - 4];
            case 2: return 0xf ^ (1 << (int)r.ReadBits(2));
            case 3: return 0xf;
            default: return code;
        }
    }

    // ----------------------------------------------------------- HP coefficients

    private static void CodeCoeffs(CodingContext ctx, Macroblock mb, BitWriter w)
    {
        var scan = mb.Orientation == 1 ? ctx.ScanVert : ctx.ScanHoriz;
        int mbits = ctx.ModelAc.FlcBits[0];
        var lapMean = new int[2];
        var localCoef = new int[32];
        bool chroma = false;

        for (var i = 0; i < 3; i++)
        {
            int pattern = mb.Cbp[i];
            for (var iBlock = 0; iBlock < 4; iBlock++)
            {
                for (var sub = 0; sub < 4; sub++, pattern >>= 1)
                {
                    int iIndex = iBlock * 4 + sub;
                    var coeffs = mb.Plane[i];
                    int off = MacroblockLayout.BlkOffset[iIndex];
                    if ((pattern & 1) != 0)
                    {
                        int n = ScanZero(coeffs, off, scan, localCoef);
                        lapMean[chroma ? 1 : 0] += n;
                        EncodeBlock(chroma, localCoef, n, ctx, CtHp, w, 1);
                    }
                }
                if (iBlock == 3) { mbits = ctx.ModelAc.FlcBits[1]; chroma = true; }
            }
        }

        ModelBits.UpdateMb(ctx.ColorFormat, ctx.Channels, lapMean, ctx.ModelAc);
    }

    private static void DecodeCoeffs(CodingContext ctx, Macroblock mb, ref BitReader r, int hpQp)
    {
        var scan = mb.Orientation == 1 ? ctx.ScanVert : ctx.ScanHoriz;
        var lapMean = new int[2];
        bool chroma = false;
        int cbp = mb.Cbp[0];

        for (var i = 0; i < 3; i++)
        {
            for (var iBlock = 0; iBlock < 4; iBlock++)
            {
                for (var sub = 0; sub < 4; sub++, cbp >>= 1)
                {
                    int iIndex = iBlock * 4 + sub;
                    var coeffs = mb.Plane[i];
                    int off = MacroblockLayout.BlkOffset[iIndex];
                    if ((cbp & 1) != 0)
                    {
                        int n = DecodeBlockHighpass(chroma, ctx, ref r, hpQp, coeffs, off, scan);
                        lapMean[chroma ? 1 : 0] += n;
                    }
                }
                if (iBlock == 3) chroma = true;
            }
            cbp = mb.Cbp[(i + 1) % 3];
        }

        ModelBits.UpdateMb(ctx.ColorFormat, ctx.Channels, lapMean, ctx.ModelAc);
    }

    // ----------------------------------------------------------- adaptive scans

    // gRes LUT (segenc.c): residual code for a non-significant coefficient (index = level + 32).
    private static readonly int[] GRes = BuildGRes();

    private static int[] BuildGRes()
    {
        var g = new int[65];
        for (var level = -32; level <= 32; level++)
            g[level + 32] = level == 0 ? 0 : level < 0 ? (2 * -level + 1) * 2 + 1 : 2 * level * 2 + 1;
        return g;
    }

    // segenc.c AdaptiveScanZero — model bits = 0 (the first-MB highpass case): pure run/level, no residual.
    private static int ScanZero(int[] coeffs, int off, AdaptiveScan scan, int[] rl)
    {
        int run = 1, n = 0;
        int level = coeffs[off + scan.Scan[1]];
        if (level != 0) { scan.Visit(1); rl[0] = 0; rl[1] = level; n = 1; run = 0; }
        for (var k = 2; k < 16; k++)
        {
            level = coeffs[off + scan.Scan[k]];
            run++;
            if (level != 0)
            {
                scan.Visit(k);
                rl[n * 2] = run - 1; rl[n * 2 + 1] = level; n++; run = 0;
            }
        }
        return n;
    }

    // segenc.c AdaptiveScan, iTrim==0 && modelBits<6 branch (the lowpass case at modelBits=4):
    // splits each coefficient into a run/level "high" part and a per-coefficient residual "low" part.
    private static int ScanLowpass(int[] coeffs, int[] residual, AdaptiveScan scan, int mbits, int[] rl)
    {
        int thOff = (1 << mbits) - 1, th = thOff * 2 + 1;
        int run = 0, n = 0;

        int s1 = scan.Scan[1], level = coeffs[s1];
        if ((uint)(level + thOff) >= (uint)th)
        {
            int abs = Math.Abs(level), hi = abs >> mbits;
            residual[s1] = (abs & thOff) * 2;
            scan.Visit(1);
            rl[0] = run; rl[1] = level < 0 ? -hi : hi; n = 1; run = 0;
        }
        else { run++; residual[s1] = GRes[level + 32]; }

        for (var k = 2; k < 16; k++)
        {
            int sk = scan.Scan[k];
            level = coeffs[sk];
            if ((uint)(level + thOff) >= (uint)th)
            {
                int sign = -(level < 0 ? 1 : 0);
                int abs = (sign ^ level) - sign, hi = abs >> mbits;
                residual[sk] = (abs & thOff) * 2;
                scan.Visit(k);
                rl[n * 2] = run; rl[n * 2 + 1] = (hi ^ sign) - sign; n++; run = 0;
            }
            else { run++; residual[sk] = GRes[level + 32]; }
        }
        return n;
    }

    // ----------------------------------------------------------- block coders (context-pool)

    private static readonly int[] EobCode = { 0, 6, 2, 7 };
    private static readonly int[] EobLen = { 1, 3, 2, 3 };

    private static void EncodeFirstIndex(bool chroma, int index, bool sign, CodingContext ctx, int offset, BitWriter w)
    {
        var ah = ctx.AHexpt[offset + (chroma ? 3 : 0)];
        ah.Discriminant += ah.Delta(index);
        ah.Discriminant1 += ah.Delta1(index);
        var (code, len) = ah.Code(index);
        w.WriteBits((uint)(code * 2 + (sign ? 1 : 0)), len + 1);
    }

    private static void EncodeIndex(bool chroma, int loc, int cont, int index, bool sign, CodingContext ctx, int offset, BitWriter w)
    {
        if (loc < 15)
        {
            var ah = ctx.AHexpt[offset + cont + 1 + (chroma ? 3 : 0)];
            ah.Discriminant += ah.Delta(index);
            ah.Discriminant1 += ah.Delta1(index);
            var (code, len) = ah.Code(index);
            w.WriteBits((uint)(code * 2 + (sign ? 1 : 0)), len + 1);
        }
        else if (loc == 15)
        {
            w.WriteBits((uint)(EobCode[index] * 2 + (sign ? 1 : 0)), EobLen[index] + 1);
        }
        else
        {
            w.WriteBits((uint)(index * 2 + (sign ? 1 : 0)), 2);
        }
    }

    private static void EncodeBlock(bool chroma, int[] coef, int n, CodingContext ctx, int offset, BitWriter w, int loc)
    {
        int lev = coef[1];
        int sr = coef[0] == 0 ? 1 : 0;
        int sl = (uint)(lev + 1) > 2u ? 1 : 0;
        int srn = n == 1 ? 0 : (coef[2] > 0 ? 2 : 1);

        EncodeFirstIndex(chroma, srn * 4 + sl * 2 + sr, lev < 0, ctx, offset, w);
        int cont = sr & srn;
        if (sl != 0) CoefficientSyntax.EncodeAbsLevel(w, ctx.AHexpt[6 + offset + cont], Math.Abs(lev));
        if (sr == 0) CoefficientSyntax.EncodeRun(w, ctx.AHexpt[0], coef[0], 15 - loc);
        loc += coef[0] + 1;

        for (var k = 1; k < n; k++)
        {
            if (srn == 2) CoefficientSyntax.EncodeRun(w, ctx.AHexpt[0], coef[k * 2], 15 - loc);
            loc += coef[k * 2] + 1;
            srn = k == n - 1 ? 0 : (coef[k * 2 + 2] > 0 ? 2 : 1);
            lev = coef[k * 2 + 1];
            sl = (uint)(lev + 1) > 2u ? 1 : 0;
            EncodeIndex(chroma, loc, cont, srn * 2 + sl, lev < 0, ctx, offset, w);
            cont &= srn;
            if (sl != 0) CoefficientSyntax.EncodeAbsLevel(w, ctx.AHexpt[6 + offset + cont], Math.Abs(lev));
        }
    }

    private static int DecodeFirstIndex(AdaptiveHuffman ah, ref BitReader r)
    {
        int index = ah.Decode(ref r);
        ah.Discriminant += ah.Delta(index);
        ah.Discriminant1 += ah.Delta1(index);
        return index;
    }

    private static int DecodeIndex(int loc, AdaptiveHuffman ah, ref BitReader r)
    {
        if (loc < 15)
        {
            int index = ah.Decode(ref r);
            ah.Discriminant += ah.Delta(index);
            ah.Discriminant1 += ah.Delta1(index);
            return index;
        }
        if (loc == 15)
        {
            if (!r.ReadBit()) return 0;
            if (!r.ReadBit()) return 2;
            return 1 + 2 * (r.ReadBit() ? 1 : 0);
        }
        return r.ReadBit() ? 1 : 0;
    }

    // segdec.c DecodeBlock — lowpass run/level pairs into aRLCoeffs.
    private static int DecodeBlock(bool chroma, int[] coef, CodingContext ctx, int offset, ref BitReader r, int loc)
    {
        int baseCtx = offset + (chroma ? 3 : 0);
        int index = DecodeFirstIndex(ctx.AHexpt[baseCtx], ref r);
        int sr = index & 1, srn = index >> 2;
        int cont = sr & srn;
        int sign = r.ReadBit() ? -1 : 0;

        coef[1] = (index & 2) != 0
            ? (CoefficientSyntax.DecodeAbsLevel(ref r, ctx.AHexpt[6 + offset + cont]) ^ sign) - sign
            : 1 | sign;
        coef[0] = sr == 0 ? CoefficientSyntax.DecodeRun(ref r, ctx.AHexpt[0], 15 - loc) : 0;
        loc += coef[0] + 1;

        int n = 1;
        while (srn != 0)
        {
            sr = srn & 1;
            coef[n * 2] = sr == 0 ? CoefficientSyntax.DecodeRun(ref r, ctx.AHexpt[0], 15 - loc) : 0;
            loc += coef[n * 2] + 1;
            index = DecodeIndex(loc, ctx.AHexpt[baseCtx + cont + 1], ref r);
            srn = index >> 1;
            cont &= srn;
            sign = r.ReadBit() ? -1 : 0;
            coef[n * 2 + 1] = (index & 1) != 0
                ? (CoefficientSyntax.DecodeAbsLevel(ref r, ctx.AHexpt[6 + offset + cont]) ^ sign) - sign
                : 1 | sign;
            n++;
        }
        return n;
    }

    // segdec.c DecodeBlockHighpass — writes dequantized highpass coefficients straight into the plane.
    private static int DecodeBlockHighpass(bool chroma, CodingContext ctx, ref BitReader r, int qp, int[] coef, int off, AdaptiveScan scan)
    {
        int baseCtx = CtHp + (chroma ? 3 : 0);
        int loc = 1;

        int index = DecodeFirstIndex(ctx.AHexpt[baseCtx], ref r);
        int sr = index & 1, srn = index >> 2;
        int cont = sr & srn;
        int sign = r.ReadBit() ? -1 : 0;

        int level = (qp ^ sign) - sign;
        if ((index & 2) != 0) level *= CoefficientSyntax.DecodeAbsLevel(ref r, ctx.AHexpt[6 + CtHp + cont]);
        if (sr == 0) loc += CoefficientSyntax.DecodeRun(ref r, ctx.AHexpt[0], 15 - loc);
        loc &= 0xf;
        coef[off + scan.Scan[loc]] = level;
        scan.Visit(loc);
        loc = (loc + 1) & 0xf;

        int n = 1;
        while (srn != 0)
        {
            sr = srn & 1;
            if (sr == 0)
            {
                loc += CoefficientSyntax.DecodeRun(ref r, ctx.AHexpt[0], 15 - loc);
                if (loc >= 16) return 16;
            }
            index = DecodeIndex(loc + 1, ctx.AHexpt[baseCtx + cont + 1], ref r);
            srn = index >> 1;
            cont &= srn;
            sign = r.ReadBit() ? -1 : 0;

            level = (qp ^ sign) - sign;
            if ((index & 1) != 0) level *= CoefficientSyntax.DecodeAbsLevel(ref r, ctx.AHexpt[6 + CtHp + cont]);
            coef[off + scan.Scan[loc]] = level;
            scan.Visit(loc);
            loc = (loc + 1) & 0xf;
            n++;
        }
        return n;
    }

    private static int Clamp8(int x) => x < -8 ? -8 : x > 7 ? 7 : x;
}
