using static SharpAstro.Jxr.PhotoOverlapTransform;

namespace SharpAstro.Jxr;

/// <summary>
/// The whole-image inverse transform driver for the <b>reduced-resolution chroma</b>
/// planes of YUV420 / YUV422 at <c>OL_ONE</c> / <c>OL_TWO</c>: it runs the chroma
/// second-stage transform, the first-level <see cref="PhotoCoreTransform"/> inverse,
/// and the Photo Overlap (POT) post-filters across the macroblock grid — the chroma
/// analogue of <see cref="OverlapTransform"/>.
///
/// <para>The reduced chroma grid has only 64 (420, an 8×8-px MB) or 128 (422, an
/// 8×16-px MB) ints per macroblock instead of 256, so the column stride and all the
/// overlap operators' offsets shrink accordingly (corner reach −64 for 420, −128 for
/// 422). As with the luma driver we use a whole-image buffer per channel with the same
/// one-MB-slack ring layout and drive the identical extended grid
/// (<c>cRow ∈ [0, mbRows], cCol ∈ [0, mbCols]</c>) — the chroma loops additionally lag
/// the left column by one MB (<c>leftAdjacentColumn</c>/<c>rightAdjacentColumn</c>) so
/// the corner overlap operators see fully-transformed neighbours.</para>
///
/// <para>Subsampled chroma is always coded in jxrlib's scaled-arithmetic mode, so the
/// second stage is always the ×2 "Dec" 2×2 (and 422's 1D Hadamard) — exactly
/// <see cref="ChromaTransform.InverseMbNoOverlap"/>'s second stage. Faithful port of the
/// <c>420_UV</c> / <c>422_UV</c> blocks of decode/strInvTransform.c
/// <c>invTransformMacroblock_alteredOperators_hard</c> (the exact-inverse "_alternate"
/// operators a standard subversion-≠0 codestream selects), single-tile (so every
/// tile-boundary predicate is constant FALSE and drops out).</para>
/// </summary>
internal static class ChromaOverlapTransform
{
    /// <summary>Ints per reduced chroma macroblock — also the column stride.</summary>
    private static int Mb(ColorFormat cf) => cf == ColorFormat.Yuv420 ? 64 : 128;

    /// <summary>Allocate the U,V reduced-chroma whole-image buffers (with the one-MB
    /// right/bottom slack the boundary processing positions index into).</summary>
    public static int[][] AllocatePlanes(int mbCols, int mbRows, ColorFormat cf)
    {
        int len = BufferLength(mbCols, mbRows, cf);
        return new[] { new int[len], new int[len] };
    }

    /// <summary>Offset (in ints) of macroblock <paramref name="mbC"/>,<paramref name="mbR"/> in a reduced-chroma buffer.</summary>
    public static int MbBase(int mbCols, int mbR, int mbC, ColorFormat cf) => mbR * RowStride(mbCols, cf) + mbC * Mb(cf);

    /// <summary>Ints per reduced-chroma MB-row (with +1 column of slack).</summary>
    public static int RowStride(int mbCols, ColorFormat cf) => (mbCols + 1) * Mb(cf);

    private static int BufferLength(int mbCols, int mbRows, ColorFormat cf) => (mbRows + 1) * RowStride(mbCols, cf);

    /// <summary>
    /// Inverse transform (chroma second stage + first-level core inverse + POT post-filter)
    /// every reduced-chroma macroblock in <paramref name="planes"/> (U,V) in place, for
    /// <paramref name="overlap"/> ∈ {1,2}. Input is the dequantized reduced coefficients
    /// (block layout); on return each buffer holds the reconstructed reduced chroma in the
    /// <c>idxCC_420</c>/<c>idxCC</c> spatial layout that <see cref="ChromaUpsample"/> upsamples.
    /// </summary>
    public static void Inverse(int[][] planes, int mbCols, int mbRows, int overlap, ColorFormat cf)
    {
        int mb = Mb(cf);
        int rowStride = RowStride(mbCols, cf);

        // Per-channel corner-prediction carry state for the OL_TWO second-level overlap
        // (jxrlib iPredBefore[i][0..1] / iPredAfter[i][0..1]; captured at the right-adjacent
        // column, consumed at the virtual right-edge column).
        var predBefore = new int[planes.Length][];
        var predAfter = new int[planes.Length][];
        for (var c = 0; c < planes.Length; c++) { predBefore[c] = new int[2]; predAfter[c] = new int[2]; }

        for (var cRow = 0; cRow <= mbRows; cRow++)
        {
            for (var cCol = 0; cCol <= mbCols; cCol++)
            {
                bool left = cCol == 0, right = cCol == mbCols, top = cRow == 0, bottom = cRow == mbRows;
                bool leftAdj = cCol == 1, rightAdj = cCol == mbCols - 1;
                int p1 = cRow * rowStride + cCol * mb;
                int p0 = p1 - rowStride;
                for (var ch = 0; ch < planes.Length; ch++)
                {
                    if (cf == ColorFormat.Yuv420)
                        InverseMb420(planes[ch], p0, p1, left, right, top, bottom, leftAdj, rightAdj, overlap, predBefore[ch], predAfter[ch]);
                    else
                        InverseMb422(planes[ch], p0, p1, left, right, top, bottom, leftAdj, rightAdj, overlap, predBefore[ch], predAfter[ch]);
                }
            }
        }
    }

    // ===================================================================== 420_UV

    // strInvTransform.c:1464-1651 — one reduced 420 chroma MB at the (p0,p1) window position.
    private static void InverseMb420(int[] b, int p0, int p1, bool left, bool right, bool top, bool bottom,
                                     bool leftAdj, bool rightAdj, int overlap, int[] predBefore, int[] predAfter)
    {
        var buf = b.AsSpan();
        bool topORbottom = top || bottom, leftORright = left || right;

        // ---- second level inverse transform (strDCT2x2dnDec on p1; scaled-arith always) ----
        if (!(bottom || right))
            PhotoCoreTransform.ChromaInverseStage2_420(buf.Slice(p1, 64));

        // ---- second level inverse overlap (OL_TWO) ----
        if (overlap == 2)
        {
            if (leftAdj && top) buf[p1 - 64 + 0] -= buf[p1 - 64 + 32];
            if (rightAdj && top) predBefore[0] = buf[p1 + 0];
            if (right && top) buf[p1 - 64 + 32] -= predBefore[0];
            if (leftAdj && bottom) buf[p0 - 64 + 16] -= buf[p0 - 64 + 48];
            if (rightAdj && bottom) predBefore[1] = buf[p0 + 16];
            if (right && bottom) buf[p0 - 64 + 48] -= predBefore[1];

            if (leftORright && !topORbottom)
            {
                if (left) Post2Alt(ref buf[p0 + 0 + 16], ref buf[p1 + 0]);
                if (right) Post2Alt(ref buf[p0 + -32 + 16], ref buf[p1 + -32]);
            }
            if (!leftORright)
            {
                if (topORbottom)
                {
                    if (top) Post2Alt(ref buf[p1 - 32], ref buf[p1]);
                    if (bottom) Post2Alt(ref buf[p0 + 16 - 32], ref buf[p0 + 16]);
                }
                else
                {
                    Post2x2Alt(ref buf[p0 - 16], ref buf[p0 + 16], ref buf[p1 - 32], ref buf[p1]);
                }
            }

            if (leftAdj && top) buf[p1 - 64 + 0] += buf[p1 - 64 + 32];
            if (rightAdj && top) predAfter[0] = buf[p1 + 0];
            if (right && top) buf[p1 - 64 + 32] += predAfter[0];
            if (leftAdj && bottom) buf[p0 - 64 + 16] += buf[p0 - 64 + 48];
            if (rightAdj && bottom) predAfter[1] = buf[p0 + 16];
            if (right && bottom) buf[p0 - 64 + 48] += predAfter[1];
        }

        // ---- first level inverse transform (strIDCT4x4Stage1, staggered) ----
        if (!top)
            for (var j = left ? 48 : (leftAdj ? -48 : -16); j < (right ? 16 : 48); j += 32)
                PhotoCoreTransform.InverseStage1(buf.Slice(p0 + j, 16));
        if (!bottom)
            for (var j = left ? 32 : (leftAdj ? -64 : -32); j < (right ? 0 : 32); j += 32)
                PhotoCoreTransform.InverseStage1(buf.Slice(p1 + j, 16));

        // ---- first level inverse overlap (OL_ONE / OL_TWO) ----
        if (overlap == 0) return;

        // Corner operations
        if (top && leftAdj) Post4Alt(ref buf[p1 - 64 + 0], ref buf[p1 - 64 + 1], ref buf[p1 - 64 + 2], ref buf[p1 - 64 + 3]);
        if (top && right) Post4Alt(ref buf[p1 - 27], ref buf[p1 - 28], ref buf[p1 - 25], ref buf[p1 - 26]);
        if (bottom && leftAdj) Post4Alt(ref buf[p0 - 64 + 16 + 10], ref buf[p0 - 64 + 16 + 11], ref buf[p0 - 64 + 16 + 8], ref buf[p0 - 64 + 16 + 9]);
        if (bottom && right) Post4Alt(ref buf[p0 - 1], ref buf[p0 - 2], ref buf[p0 - 3], ref buf[p0 - 4]);

        if (!left && !top)
        {
            int p;
            if (leftAdj)
            {
                if (!bottom)
                {
                    Post4Alt(ref buf[p0 - 64 + 26], ref buf[p0 - 64 + 24], ref buf[p1 - 64 + 0], ref buf[p1 - 64 + 2]);
                    Post4Alt(ref buf[p0 - 64 + 27], ref buf[p0 - 64 + 25], ref buf[p1 - 64 + 1], ref buf[p1 - 64 + 3]);
                }
                Post4Alt(ref buf[p0 - 64 + 10], ref buf[p0 - 64 + 8], ref buf[p0 - 64 + 16], ref buf[p0 - 64 + 18]);
                Post4Alt(ref buf[p0 - 64 + 11], ref buf[p0 - 64 + 9], ref buf[p0 - 64 + 17], ref buf[p0 - 64 + 19]);
            }
            if (bottom)
            {
                p = p0 + -48;
                Post4Alt(ref buf[p + 15], ref buf[p + 14], ref buf[p + 42], ref buf[p + 43]);
                Post4Alt(ref buf[p + 13], ref buf[p + 12], ref buf[p + 40], ref buf[p + 41]);
                if (!right)
                {
                    p = p0 + -16;
                    Post4Alt(ref buf[p + 15], ref buf[p + 14], ref buf[p + 42], ref buf[p + 43]);
                    Post4Alt(ref buf[p + 13], ref buf[p + 12], ref buf[p + 40], ref buf[p + 41]);
                }
            }
            else
            {
                PostStage1SplitAlt(buf, p0 + -48, p1 - 16 + -48, 32);
                if (!right)
                    PostStage1SplitAlt(buf, p0 + -16, p1 - 16 + -16, 32);
            }
            if (right)
            {
                if (!bottom)
                {
                    Post4Alt(ref buf[p0 - 2], ref buf[p0 - 4], ref buf[p1 - 28], ref buf[p1 - 26]);
                    Post4Alt(ref buf[p0 - 1], ref buf[p0 - 3], ref buf[p1 - 27], ref buf[p1 - 25]);
                }
                Post4Alt(ref buf[p0 - 18], ref buf[p0 - 20], ref buf[p0 - 12], ref buf[p0 - 10]);
                Post4Alt(ref buf[p0 - 17], ref buf[p0 - 19], ref buf[p0 - 11], ref buf[p0 - 9]);
            }
            else
            {
                PostStage1Alt(buf, p0 - 32, 32);
            }
            PostStage1Alt(buf, p0 - 64, 32);
        }

        if (top)
        {
            int p;
            if (!left)
            {
                p = p1 + -64 + 4;
                Post4Alt(ref buf[p + 1], ref buf[p + 0], ref buf[p + 28], ref buf[p + 29]);
                Post4Alt(ref buf[p + 3], ref buf[p + 2], ref buf[p + 30], ref buf[p + 31]);
            }
            if (!left && !right)
            {
                p = p1 + -32 + 4;
                Post4Alt(ref buf[p + 1], ref buf[p + 0], ref buf[p + 28], ref buf[p + 29]);
                Post4Alt(ref buf[p + 3], ref buf[p + 2], ref buf[p + 30], ref buf[p + 31]);
            }
        }
    }

    // ===================================================================== 422_UV

    // strInvTransform.c:1654-1885 — one reduced 422 chroma MB at the (p0,p1) window position.
    private static void InverseMb422(int[] b, int p0, int p1, bool left, bool right, bool top, bool bottom,
                                     bool leftAdj, bool rightAdj, int overlap, int[] predBefore, int[] predAfter)
    {
        var buf = b.AsSpan();
        bool topORbottom = top || bottom, leftORright = left || right;

        // ---- second level inverse transform (1D HT + 2× strDCT2x2dnDec on p1) ----
        if (!(bottom || right))
            PhotoCoreTransform.ChromaInverseStage2_422(buf.Slice(p1, 128));

        // ---- second level inverse overlap (OL_TWO) ----
        if (overlap == 2)
        {
            if (leftAdj && top) buf[p1 - 128 + 0] -= buf[p1 - 128 + 64];
            if (rightAdj && top) predBefore[0] = buf[p1 + 0];
            if (right && top) buf[p1 - 128 + 64] -= predBefore[0];
            if (leftAdj && bottom) buf[p0 - 128 + 48] -= buf[p0 - 128 + 112];
            if (rightAdj && bottom) predBefore[1] = buf[p0 + 48];
            if (right && bottom) buf[p0 - 128 + 112] -= predBefore[1];

            if (!bottom)
            {
                if (leftORright)
                {
                    if (!top)
                    {
                        if (left) Post2Alt(ref buf[p0 + 48 + 0], ref buf[p1 + 0]);
                        if (right) Post2Alt(ref buf[p0 + 48 + -64], ref buf[p1 + -64]);
                    }
                    if (left) Post2Alt(ref buf[p1 + 16], ref buf[p1 + 16 + 16]);
                    if (right) Post2Alt(ref buf[p1 + -48], ref buf[p1 + -48 + 16]);
                }
                if (!leftORright)
                {
                    if (top) Post2Alt(ref buf[p1 - 64], ref buf[p1]);
                    else Post2x2Alt(ref buf[p0 - 16], ref buf[p0 + 48], ref buf[p1 - 64], ref buf[p1]);
                    Post2x2Alt(ref buf[p1 - 48], ref buf[p1 + 16], ref buf[p1 - 32], ref buf[p1 + 32]);
                }
            }
            if (bottom && !leftORright)
                Post2Alt(ref buf[p0 - 16], ref buf[p0 + 48]);

            if (leftAdj && top) buf[p1 - 128 + 0] += buf[p1 - 128 + 64];
            if (rightAdj && top) predAfter[0] = buf[p1 + 0];
            if (right && top) buf[p1 - 128 + 64] += predAfter[0];
            if (leftAdj && bottom) buf[p0 - 128 + 48] += buf[p0 - 128 + 112];
            if (rightAdj && bottom) predAfter[1] = buf[p0 + 48];
            if (right && bottom) buf[p0 - 128 + 112] += predAfter[1];
        }

        // ---- first level inverse transform (staggered; 422 has no vertical downsampling) ----
        if (!top)
            for (var j = left ? 112 : (leftAdj ? -80 : -16); j < (right ? 48 : 112); j += 64)
                PhotoCoreTransform.InverseStage1(buf.Slice(p0 + j, 16));
        if (!bottom)
            for (var j = left ? 64 : (leftAdj ? -128 : -64); j < (right ? 0 : 64); j += 64)
            {
                PhotoCoreTransform.InverseStage1(buf.Slice(p1 + j + 0, 16));
                PhotoCoreTransform.InverseStage1(buf.Slice(p1 + j + 16, 16));
                PhotoCoreTransform.InverseStage1(buf.Slice(p1 + j + 32, 16));
            }

        // ---- first level inverse overlap (OL_ONE / OL_TWO) ----
        if (overlap == 0) return;

        int p;
        // Corner operations
        if (top && leftAdj) Post4Alt(ref buf[p1 - 128 + 0], ref buf[p1 - 128 + 1], ref buf[p1 - 128 + 2], ref buf[p1 - 128 + 3]);
        if (top && right) Post4Alt(ref buf[p1 - 59], ref buf[p1 - 60], ref buf[p1 - 57], ref buf[p1 - 58]);
        if (bottom && leftAdj) Post4Alt(ref buf[p0 - 128 + 48 + 10], ref buf[p0 - 128 + 48 + 11], ref buf[p0 - 128 + 48 + 8], ref buf[p0 - 128 + 48 + 9]);
        if (bottom && right) Post4Alt(ref buf[p0 - 1], ref buf[p0 - 2], ref buf[p0 - 3], ref buf[p0 - 4]);

        if (!top)
        {
            if (leftAdj)
            {
                p = p0 + 32 + 10 - 128;
                Post4Alt(ref buf[p + 0], ref buf[p - 2], ref buf[p + 6], ref buf[p + 8]);
                Post4Alt(ref buf[p + 1], ref buf[p - 1], ref buf[p + 7], ref buf[p + 9]);
            }
            if (right)
            {
                p = p0 + -32 + 14;
                Post4Alt(ref buf[p + 0], ref buf[p - 2], ref buf[p + 6], ref buf[p + 8]);
                Post4Alt(ref buf[p + 1], ref buf[p - 1], ref buf[p + 7], ref buf[p + 9]);
            }
            for (var j = left ? 0 : -128; j < (right ? -64 : 0); j += 64)
                PostStage1Alt(buf, p0 + j + 32, 0);
        }

        if (!bottom)
        {
            if (leftAdj)
            {
                p = p1 + 0 + 10 - 128;
                Post4Alt(ref buf[p + 0], ref buf[p - 2], ref buf[p + 6], ref buf[p + 8]);
                Post4Alt(ref buf[p + 1], ref buf[p - 1], ref buf[p + 7], ref buf[p + 9]);
                p += 16;
                Post4Alt(ref buf[p + 0], ref buf[p - 2], ref buf[p + 6], ref buf[p + 8]);
                Post4Alt(ref buf[p + 1], ref buf[p - 1], ref buf[p + 7], ref buf[p + 9]);
            }
            if (right)
            {
                p = p1 + -64 + 14;
                Post4Alt(ref buf[p + 0], ref buf[p - 2], ref buf[p + 6], ref buf[p + 8]);
                Post4Alt(ref buf[p + 1], ref buf[p - 1], ref buf[p + 7], ref buf[p + 9]);
                p += 16;
                Post4Alt(ref buf[p + 0], ref buf[p - 2], ref buf[p + 6], ref buf[p + 8]);
                Post4Alt(ref buf[p + 1], ref buf[p - 1], ref buf[p + 7], ref buf[p + 9]);
            }
            for (var j = left ? 0 : -128; j < (right ? -64 : 0); j += 64)
            {
                PostStage1Alt(buf, p1 + j + 0, 0);
                PostStage1Alt(buf, p1 + j + 16, 0);
            }
        }

        if (topORbottom)
        {
            if (top)
            {
                p = p1 + 5;
                for (var j = left ? 0 : -128; j < (right ? -64 : 0); j += 64)
                {
                    Post4Alt(ref buf[p + j + 0], ref buf[p + j - 1], ref buf[p + j + 59], ref buf[p + j + 60]);
                    Post4Alt(ref buf[p + j + 2], ref buf[p + j + 1], ref buf[p + j + 61], ref buf[p + j + 62]);
                }
            }
            if (bottom)
            {
                p = p0 + 48 + 13;
                for (var j = left ? 0 : -128; j < (right ? -64 : 0); j += 64)
                {
                    Post4Alt(ref buf[p + j + 0], ref buf[p + j - 1], ref buf[p + j + 59], ref buf[p + j + 60]);
                    Post4Alt(ref buf[p + j + 2], ref buf[p + j + 1], ref buf[p + j + 61], ref buf[p + j + 62]);
                }
            }
        }
        else
        {
            if (leftAdj)
            {
                int j = 0 + 0 - 128;
                Post4Alt(ref buf[p0 + j + 48 + 10 + 0], ref buf[p0 + j + 48 + 10 - 2], ref buf[p1 + j + 0], ref buf[p1 + j + 2]);
                Post4Alt(ref buf[p0 + j + 48 + 10 + 1], ref buf[p0 + j + 48 + 10 - 1], ref buf[p1 + j + 1], ref buf[p1 + j + 3]);
            }
            if (right)
            {
                int j = -64 + 4;
                Post4Alt(ref buf[p0 + j + 48 + 10 + 0], ref buf[p0 + j + 48 + 10 - 2], ref buf[p1 + j + 0], ref buf[p1 + j + 2]);
                Post4Alt(ref buf[p0 + j + 48 + 10 + 1], ref buf[p0 + j + 48 + 10 - 1], ref buf[p1 + j + 1], ref buf[p1 + j + 3]);
            }
            for (var j = left ? 0 : -128; j < (right ? -64 : 0); j += 64)
                PostStage1SplitAlt(buf, p0 + j + 48, p1 + j + 0, 0);
        }
    }
}
