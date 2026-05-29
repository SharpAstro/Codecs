namespace SharpAstro.Jxr;

/// <summary>
/// The whole-image transform driver for <b>YUV444</b>: runs the two-stage Photo
/// Core Transform (and, once wired, the Photo Overlap pre-/post-filter) across a
/// grid of macroblocks, reproducing jxrlib's sliding two-MB-row window exactly.
///
/// <para>jxrlib processes one MB row at a time through a 2-row strip
/// (<c>p0</c> = the row above, <c>p1</c> = the current row; column stride 256,
/// <c>p0 = p1 - rowStride</c>) with a <b>one-MB lag</b> in both axes: the transform
/// at processing position <c>(cColumn, cRow)</c> finalizes the macroblock at
/// <c>(cColumn-1, cRow-1)</c> (strenc.c <c>processMacroblock</c>), and a Term pass
/// flushes the trailing row/column. We get the identical byte-for-byte result with
/// a simpler model: a <b>whole-image</b> coefficient buffer per channel (block-major,
/// the same column stride 256 so <c>p0 = p1 - rowStride</c>), driven over the same
/// extended grid (<c>cRow ∈ [0, mbRows], cCol ∈ [0, mbCols]</c>) with the same edge
/// flags. Because the overlap/transform at a position only reads the current row and
/// the row above (never below), pre-loading every row changes nothing — each position
/// touches the same buffer cells in the same order as jxrlib's strip.</para>
///
/// <para>Faithful port of the YUV444 branch of:
/// <list type="bullet">
/// <item>encode/strFwdTransform.c <c>transformMacroblock</c> (forward),</item>
/// <item>decode/strInvTransform.c <c>invTransformMacroblock_alteredOperators_hard</c>
/// (inverse — the exact-inverse "_alternate" operators a standard subversion-1
/// codestream selects).</item>
/// </list>
/// Single-tile only, so every tile-boundary predicate
/// (<c>bVertTileBoundary</c>/<c>bHoriTileBoundary</c>/<c>bOneMB*</c>) is constant
/// FALSE and drops out, leaving just the top/bottom/left/right edge flags.</para>
/// </summary>
internal static class OverlapTransform
{
    private const int Channels = 3;
    private const int Mb = 256; // one macroblock plane (16×16) — also the column stride

    /// <summary>Allocate the three whole-image channel buffers (block-major, with the
    /// one-MB right/bottom slack the boundary processing positions index into).</summary>
    public static int[][] AllocatePlanes(int mbCols, int mbRows)
    {
        int len = BufferLength(mbCols, mbRows);
        return new[] { new int[len], new int[len], new int[len] };
    }

    /// <summary>Byte offset (in ints) of macroblock <paramref name="mbC"/>,<paramref name="mbR"/> in a channel buffer.</summary>
    public static int MbBase(int mbCols, int mbR, int mbC) => mbR * RowStride(mbCols) + mbC * Mb;

    private static int RowStride(int mbCols) => (mbCols + 1) * Mb;        // +1 column of slack
    private static int BufferLength(int mbCols, int mbRows) => (mbRows + 1) * RowStride(mbCols); // +1 row of slack

    // ===================================================================== forward

    /// <summary>
    /// Forward transform every macroblock in <paramref name="planes"/> in place.
    /// On return each channel buffer holds the finalized (pre-quantization)
    /// coefficients in block-major layout, ready for per-MB quantize + entropy code.
    /// </summary>
    public static void Forward(int[][] planes, int mbCols, int mbRows, int overlap, bool scaledArith)
    {
        int rowStride = RowStride(mbCols);
        for (var cRow = 0; cRow <= mbRows; cRow++)
        {
            for (var cCol = 0; cCol <= mbCols; cCol++)
            {
                bool left = cCol == 0, right = cCol == mbCols, top = cRow == 0, bottom = cRow == mbRows;
                int p1 = cRow * rowStride + cCol * Mb;
                int p0 = p1 - rowStride;
                for (var ch = 0; ch < Channels; ch++)
                    ForwardMb(planes[ch], p0, p1, left, right, top, bottom, overlap, scaledArith, ch != 0);
            }
        }
    }

    private static void ForwardMb(int[] b, int p0, int p1, bool left, bool right, bool top, bool bottom,
                                  int overlap, bool scaledArith, bool chroma)
    {
        var buf = b.AsSpan();

        // ---- first level overlap (OL_ONE / OL_TWO) — wired in Rung 7f.2 ----

        // ---- first level transform (strDCT4x4Stage1, staggered across the MB window) ----
        if (!top)
        {
            for (var j = left ? 48 : -16; j < (right ? 48 : 240); j += 64)
                PhotoCoreTransform.ForwardStage1(buf.Slice(p0 + j, 16));
        }
        if (!bottom)
        {
            for (var j = left ? 0 : -64; j < (right ? 0 : 192); j += 64)
            {
                PhotoCoreTransform.ForwardStage1(buf.Slice(p1 + j + 0, 16));
                PhotoCoreTransform.ForwardStage1(buf.Slice(p1 + j + 16, 16));
                PhotoCoreTransform.ForwardStage1(buf.Slice(p1 + j + 32, 16));
            }
        }

        // ---- second level overlap (OL_TWO) — wired in Rung 7f.3 ----

        // ---- second level transform (strDCT4x4SecondStage on the lagged MB at p0-256) ----
        if (!(top || left))
        {
            if (scaledArith) PhotoCoreTransform.NormalizeEnc(buf.Slice(p0 - Mb, Mb), chroma);
            PhotoCoreTransform.ForwardStage2(buf.Slice(p0 - Mb, Mb));
        }
    }

    // ===================================================================== inverse

    /// <summary>
    /// Inverse transform every macroblock in <paramref name="planes"/> in place.
    /// Input is the dequantized coefficients (block-major); on return each channel
    /// buffer holds the reconstructed post-color-transform samples.
    /// </summary>
    public static void Inverse(int[][] planes, int mbCols, int mbRows, int overlap, bool scaledArith)
    {
        int rowStride = RowStride(mbCols);
        for (var cRow = 0; cRow <= mbRows; cRow++)
        {
            for (var cCol = 0; cCol <= mbCols; cCol++)
            {
                bool left = cCol == 0, right = cCol == mbCols, top = cRow == 0, bottom = cRow == mbRows;
                int p1 = cRow * rowStride + cCol * Mb;
                int p0 = p1 - rowStride;
                for (var ch = 0; ch < Channels; ch++)
                    InverseMb(planes[ch], p0, p1, left, right, top, bottom, overlap, scaledArith, ch != 0);
            }
        }
    }

    private static void InverseMb(int[] b, int p0, int p1, bool left, bool right, bool top, bool bottom,
                                  int overlap, bool scaledArith, bool chroma)
    {
        var buf = b.AsSpan();

        // ---- second level inverse transform (strIDCT4x4Stage2 on p1) ----
        if (!(bottom || right))
        {
            PhotoCoreTransform.InverseStage2(buf.Slice(p1, Mb));
            if (scaledArith) PhotoCoreTransform.NormalizeDec(buf.Slice(p1, Mb), chroma);
        }

        // ---- second level inverse overlap (OL_TWO) — wired in Rung 7f.3 ----

        // ---- first level inverse transform (strIDCT4x4Stage1, staggered across the MB window) ----
        if (!top)
        {
            for (var j = left ? 32 : -96; j < (right ? 32 : 160); j += 64)
            {
                PhotoCoreTransform.InverseStage1(buf.Slice(p0 + j + 0, 16));
                PhotoCoreTransform.InverseStage1(buf.Slice(p0 + j + 16, 16));
            }
        }
        if (!bottom)
        {
            for (var j = left ? 0 : -128; j < (right ? 0 : 128); j += 64)
            {
                PhotoCoreTransform.InverseStage1(buf.Slice(p1 + j + 0, 16));
                PhotoCoreTransform.InverseStage1(buf.Slice(p1 + j + 16, 16));
            }
        }

        // ---- first level inverse overlap (OL_ONE / OL_TWO) — wired in Rung 7f.2 ----
    }
}
