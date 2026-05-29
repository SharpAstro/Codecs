namespace SharpAstro.Jxr;

/// <summary>Internal processing color format (jxrlib <c>COLORFORMAT</c>).</summary>
internal enum ColorFormat
{
    YOnly = 0,
    Yuv420 = 1,
    Yuv422 = 2,
    Yuv444 = 3,
    Cmyk = 4,
    NComponent = 6,
}

/// <summary>
/// Per-macroblock prediction state carried to neighboring MBs (jxrlib
/// <c>CWMIPredInfo</c>): the DC value, the LP "AD" coefficients (first row + first
/// column of the DC block), and the LP QP index used.
/// </summary>
internal sealed class PredInfo
{
    public int Dc;
    public int QpIndex;
    /// <summary>copyAC layout: [0..2] = DC-block cells 1,2,3 (first row); [3..5] = cells 4,8,12 (first column).</summary>
    public readonly int[] Ad = new int[6];
}

/// <summary>
/// JPEG XR DC / LP-AD / AC prediction, ported faithfully from jxrlib
/// (image/sys/strPredQuant.c mode selectors; image/encode/strPredQuantEnc.c
/// <c>predMacroblockEnc</c>; image/decode/strPredQuantDec.c <c>predDCACDec</c> /
/// <c>predACDec</c>). This covers the full-resolution path (Y_ONLY / YUV_444 /
/// CMYK / NCOMPONENT — sixteen 4×4 blocks per channel). YUV 4:2:0 / 4:2:2 chroma
/// subsampling prediction is deferred.
/// </summary>
/// <remarks>
/// DC/AD prediction is a plain neighbor subtract (encode) / add (decode) on the
/// DC block. AC prediction is a differential along the first row/column of HP
/// coefficients within the macroblock: the encoder subtracts in reverse block
/// order so each reference is still original; the decoder adds in forward order
/// using already-reconstructed references — so encode∘decode round-trips exactly.
/// <c>ORIENT_WEIGHT = 4</c> drives the horizontal-vs-vertical mode choice.
/// </remarks>
internal static class Prediction
{
    private const int OrientWeight = 4;

    // strPredQuant.c:200 copyAC — first row (1,2,3) + first column (4,8,12) of a DC block.
    public static void CopyAc(ReadOnlySpan<int> block, Span<int> ad)
    {
        ad[0] = block[1]; ad[1] = block[2]; ad[2] = block[3];
        ad[3] = block[4]; ad[4] = block[8]; ad[5] = block[12];
    }

    /// <summary>
    /// strPredQuant.c:166 getACPredMode — 0 (from left), 1 (from top), 2 (none),
    /// from the first-row vs first-column energy of the DC block(s).
    /// <paramref name="blockDc"/> holds one int[16] per channel.
    /// </summary>
    public static int GetAcPredMode(int[][] blockDc, ColorFormat cf)
    {
        int[] y = blockDc[0];
        int strH = Math.Abs(y[1]) + Math.Abs(y[2]) + Math.Abs(y[3]);
        int strV = Math.Abs(y[4]) + Math.Abs(y[8]) + Math.Abs(y[12]);

        if (cf != ColorFormat.YOnly && cf != ColorFormat.NComponent)
        {
            int[] u = blockDc[1], v = blockDc[2];
            strH += Math.Abs(u[1]) + Math.Abs(v[1]);
            if (cf == ColorFormat.Yuv420)
            {
                strV += Math.Abs(u[2]) + Math.Abs(v[2]);
            }
            else if (cf == ColorFormat.Yuv422)
            {
                strV += Math.Abs(u[2]) + Math.Abs(v[2]) + Math.Abs(u[6]) + Math.Abs(v[6]);
                strH += Math.Abs(u[5]) + Math.Abs(v[5]);
            }
            else // YUV_444 or CMYK
            {
                strV += Math.Abs(u[4]) + Math.Abs(v[4]);
            }
        }

        return strH * OrientWeight < strV ? 1 : (strV * OrientWeight < strH ? 0 : 2);
    }

    /// <summary>
    /// strPredQuant.c:194 getDCACPredMode — packed result: bits[1:0] DC mode
    /// (0 left, 1 top, 2 mean, 3 none), bits[3:2] AD mode (0 left, 1 top, 2 none).
    /// Neighbor DCs are the per-channel left / top / top-left values; the chroma
    /// terms apply for YUV formats (scale 8/4/2 for 420/422/444).
    /// </summary>
    public static int GetDcAcPredMode(
        bool ctxLeft, bool ctxTop, ColorFormat cf,
        int dcLeftY, int dcTopY, int dcTopLeftY,
        int qIndexLp, int qLeftY, int qTopY,
        (int left, int top, int topLeft)[]? chromaDc = null)
    {
        int dcMode;
        if (ctxLeft && ctxTop) dcMode = 3;          // top-left corner — no prediction
        else if (ctxLeft) dcMode = 1;               // left column — predict from top
        else if (ctxTop) dcMode = 0;                // top row — predict from left
        else
        {
            int strH, strV;
            if (cf == ColorFormat.YOnly || cf == ColorFormat.NComponent)
            {
                strH = Math.Abs(dcTopLeftY - dcLeftY);
                strV = Math.Abs(dcTopLeftY - dcTopY);
            }
            else
            {
                int scale = cf == ColorFormat.Yuv420 ? 8 : (cf == ColorFormat.Yuv422 ? 4 : 2);
                var u = chromaDc![0];
                var v = chromaDc[1];
                strH = Math.Abs(dcTopLeftY - dcLeftY) * scale + Math.Abs(u.topLeft - u.left) + Math.Abs(v.topLeft - v.left);
                strV = Math.Abs(dcTopLeftY - dcTopY) * scale + Math.Abs(u.topLeft - u.top) + Math.Abs(v.topLeft - v.top);
            }
            dcMode = strH * OrientWeight < strV ? 1 : (strV * OrientWeight < strH ? 0 : 2);
        }

        int adMode = 2;
        if (dcMode == 1 && qIndexLp == qTopY) adMode = 1;
        if (dcMode == 0 && qIndexLp == qLeftY) adMode = 0;
        return dcMode + (adMode << 2);
    }

    // ---- DC + AD prediction on the DC block (full-resolution channel) ----

    /// <summary>Encode-side DC+AD prediction subtract for one full-resolution channel.</summary>
    public static void DcAdPredictEnc(Span<int> block, int dcMode, int adMode, PredInfo left, PredInfo top)
    {
        if (dcMode == 1) block[0] -= top.Dc;
        else if (dcMode == 0) block[0] -= left.Dc;
        else if (dcMode == 2) block[0] -= (left.Dc + top.Dc) >> 1;

        if (adMode == 1) { block[4] -= top.Ad[3]; block[8] -= top.Ad[4]; block[12] -= top.Ad[5]; }
        else if (adMode == 0) { block[1] -= left.Ad[0]; block[2] -= left.Ad[1]; block[3] -= left.Ad[2]; }
    }

    /// <summary>Decode-side DC+AD prediction add for one full-resolution channel.</summary>
    public static void DcAdPredictDec(Span<int> block, int dcMode, int adMode, PredInfo left, PredInfo top)
    {
        if (dcMode == 1) block[0] += top.Dc;
        else if (dcMode == 0) block[0] += left.Dc;
        else if (dcMode == 2) block[0] += (left.Dc + top.Dc) >> 1;

        if (adMode == 1) { block[4] += top.Ad[3]; block[8] += top.Ad[4]; block[12] += top.Ad[5]; }
        else if (adMode == 0) { block[1] += left.Ad[0]; block[2] += left.Ad[1]; block[3] += left.Ad[2]; }
    }

    // ---- AC prediction on the 256-coefficient MB plane buffer ----
    // Decode block order (forward); encode uses the reverse so refs stay original.
    private static readonly int[] AcTopBlocks = { 1, 2, 3, 5, 6, 7, 9, 10, 11, 13, 14, 15 };

    /// <summary>Encode-side AC prediction subtract on a 256-coefficient plane buffer.</summary>
    public static void AcPredictEnc(Span<int> plane, int acPredMode)
    {
        if (acPredMode == 1) // from top
        {
            for (var k = 0; k <= 192; k += 64)
                for (var j = 48; j > 0; j -= 16)
                {
                    plane[k + j + 10] -= plane[k + j + 10 - 16];
                    plane[k + j + 2] -= plane[k + j + 2 - 16];
                    plane[k + j + 9] -= plane[k + j + 9 - 16];
                }
        }
        else if (acPredMode == 0) // from left
        {
            for (var k = 0; k < 64; k += 16)
                for (var j = 192; j > 0; j -= 64)
                {
                    plane[k + j + 5] -= plane[k + j + 5 - 64];
                    plane[k + j + 1] -= plane[k + j + 1 - 64];
                    plane[k + j + 6] -= plane[k + j + 6 - 64];
                }
        }
    }

    /// <summary>Decode-side AC prediction add on a 256-coefficient plane buffer.</summary>
    public static void AcPredictDec(Span<int> plane, int acPredMode)
    {
        if (acPredMode == 1) // from top
        {
            foreach (var blk in AcTopBlocks)
            {
                int o = 16 * blk;
                plane[o + 2] += plane[o - 16 + 2];
                plane[o + 10] += plane[o - 16 + 10];
                plane[o + 9] += plane[o - 16 + 9];
            }
        }
        else if (acPredMode == 0) // from left
        {
            for (var j = 64; j < 256; j += 16)
            {
                plane[j + 1] += plane[j - 64 + 1];
                plane[j + 5] += plane[j - 64 + 5];
                plane[j + 6] += plane[j - 64 + 6];
            }
        }
    }
}
