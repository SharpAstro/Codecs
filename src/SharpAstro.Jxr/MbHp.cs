namespace SharpAstro.Jxr;

/// <summary>
/// Macroblock-level HP-band orchestrator — T.832 §8.7.18.2 (MB_HP).
/// Iterates the 16 blocks of a Luma component (or 16 blocks per Chroma
/// component for RGB/NComponent/YUV444 formats) in hierarchical scan
/// order, dispatches each non-zero block through
/// <see cref="BlockAdaptive"/>, and accumulates iLapMean for the
/// CoefficientModel update at MB-end.
/// </summary>
/// <remarks>
/// This first implementation handles the simplest case: <b>Luma-only
/// MBs</b> (NumComponents = 1, INTERNAL_CLR_FMT = YOnly). Multi-component
/// formats (RGB, YUV444 with chroma) follow the same shape and will land
/// alongside CBPHP signalling in a follow-on commit.
///
/// The 16-block hierarchical scan order
/// <c>{0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15}</c> visits
/// blocks in a Morton-like (z-order) traversal that places nearby
/// blocks together — important for adjacent-block prediction state
/// continuity. The CBPHP bitmap is walked LSB-first matching this
/// iteration order, so bit <c>k</c> of CBPHP corresponds to the
/// <c>k</c>-th MB-iteration, NOT to raster position <c>k</c>.
/// </remarks>
public static class MbHp
{
    /// <summary>Hierarchical scan order — T.832 §8.7.18.2.</summary>
    public static ReadOnlySpan<byte> HierScanOrder =>
        [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];

    /// <summary>
    /// Encode the HP-band coefficients of one Luma-only macroblock.
    /// </summary>
    /// <param name="writer">Bit-stream writer.</param>
    /// <param name="state">Mutable MB-level state (updated as encoding progresses).</param>
    /// <param name="mbhpMode">MBHPMode (0=predict-from-left, 1=predict-from-top, 2=no-prediction). Selects scan direction: 0/2 → horizontal, 1 → vertical.</param>
    /// <param name="blocks">256 ints = 16 blocks of 16 raster-order positions each (position 0 is DC, 1..15 are AC).</param>
    /// <returns>The 16-bit CBPHP bitmap (bit k = "k-th iteration block had any non-zero AC"), to be signalled separately.</returns>
    public static int EncodeLumaMb(
        BitWriter writer,
        MbHpState state,
        int mbhpMode,
        ReadOnlySpan<int> blocks)
    {
        if (blocks.Length < 256)
            throw new ArgumentException("blocks must hold 16 blocks * 16 positions = 256 ints", nameof(blocks));

        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;
        var cbphp = 0;
        var totalNonZero = 0;

        for (var i = 0; i < 16; i++)
        {
            var iBlockMap = (int)HierScanOrder[i];
            var blockSpan = blocks.Slice(iBlockMap * 16, 16);

            // Detect any non-zero in AC positions 1..15.
            var hasNonZero = false;
            for (var p = 1; p < 16; p++)
            {
                if (blockSpan[p] != 0)
                {
                    hasNonZero = true;
                    break;
                }
            }

            if (hasNonZero)
            {
                cbphp |= 1 << i;
                totalNonZero += EncodeBlock(writer, state, scan, bChroma: false, blockSpan);
            }
        }

        // Per-MB Model update (T.832 8.12.2). For luma-only, only iLapMean[0] is
        // populated; iLapMean[1] stays 0 and is unused under format=YOnly.
        CoefficientModel.Update(
            ref state.Model,
            iLapMean0: totalNonZero,
            iLapMean1: 0,
            CoefficientModel.Band.Hp,
            JxrInternalColorFormat.YOnly,
            numComponents: 1);

        return cbphp;
    }

    /// <summary>
    /// Decode the HP-band coefficients of one Luma-only macroblock. Writes
    /// reconstructed coefficients into <paramref name="blocks"/> (256 ints).
    /// </summary>
    public static void DecodeLumaMb(
        ref BitReader reader,
        MbHpState state,
        int mbhpMode,
        int cbphp,
        Span<int> blocks)
    {
        if (blocks.Length < 256)
            throw new ArgumentException("blocks must hold 16 blocks * 16 positions = 256 ints", nameof(blocks));

        // Zero AC positions; DC position 0 of each block is the caller's responsibility.
        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
                blocks[b * 16 + p] = 0;

        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;
        var totalNonZero = 0;

        for (var i = 0; i < 16; i++)
        {
            if ((cbphp & (1 << i)) == 0) continue;
            var iBlockMap = (int)HierScanOrder[i];
            totalNonZero += DecodeBlock(ref reader, state, scan, bChroma: false, blocks.Slice(iBlockMap * 16, 16));
        }

        CoefficientModel.Update(
            ref state.Model,
            iLapMean0: totalNonZero,
            iLapMean1: 0,
            CoefficientModel.Band.Hp,
            JxrInternalColorFormat.YOnly,
            numComponents: 1);
    }

    /// <summary>
    /// Encode the HP-band of one multi-component macroblock for formats
    /// where every component has 16 blocks (4×4): RGB, NComponent, YUV444,
    /// YUVK. Component 0 is treated as Luma, components 1..N-1 as Chroma.
    /// </summary>
    /// <param name="blocks"><c>numComponents * 256</c> ints, contiguous per component.</param>
    /// <param name="cbphpOut">Per-component CBPHP bitmaps (length ≥ numComponents). Populated on return.</param>
    public static void EncodeMb(
        BitWriter writer,
        MbHpState state,
        int mbhpMode,
        JxrInternalColorFormat format,
        int numComponents,
        ReadOnlySpan<int> blocks,
        Span<int> cbphpOut)
    {
        EnsureFullSizeChroma(format);
        if (blocks.Length < numComponents * 256)
            throw new ArgumentException($"blocks must hold {numComponents} components × 256 ints", nameof(blocks));
        if (cbphpOut.Length < numComponents)
            throw new ArgumentException($"cbphpOut must hold {numComponents} ints", nameof(cbphpOut));

        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;
        var iLapMeanLum = 0;
        var iLapMeanChr = 0;

        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var componentBlocks = blocks.Slice(c * 256, 256);
            var componentCbphp = 0;

            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                var blockSpan = componentBlocks.Slice(iBlockMap * 16, 16);

                var hasNonZero = false;
                for (var p = 1; p < 16; p++)
                {
                    if (blockSpan[p] != 0)
                    {
                        hasNonZero = true;
                        break;
                    }
                }

                if (hasNonZero)
                {
                    componentCbphp |= 1 << i;
                    var n = EncodeBlock(writer, state, scan, bChroma, blockSpan);
                    if (bChroma) iLapMeanChr += n;
                    else iLapMeanLum += n;
                }
            }

            cbphpOut[c] = componentCbphp;
        }

        CoefficientModel.Update(
            ref state.Model,
            iLapMean0: iLapMeanLum,
            iLapMean1: iLapMeanChr,
            CoefficientModel.Band.Hp,
            format,
            numComponents);
    }

    /// <summary>Decode the HP-band of one multi-component macroblock — dual of <see cref="EncodeMb"/>.</summary>
    public static void DecodeMb(
        ref BitReader reader,
        MbHpState state,
        int mbhpMode,
        JxrInternalColorFormat format,
        int numComponents,
        ReadOnlySpan<int> cbphpPerComponent,
        Span<int> blocks)
    {
        EnsureFullSizeChroma(format);
        if (blocks.Length < numComponents * 256)
            throw new ArgumentException($"blocks must hold {numComponents} components × 256 ints", nameof(blocks));
        if (cbphpPerComponent.Length < numComponents)
            throw new ArgumentException($"cbphpPerComponent must hold {numComponents} ints", nameof(cbphpPerComponent));

        // Zero AC positions; DC positions are caller's responsibility.
        for (var c = 0; c < numComponents; c++)
            for (var b = 0; b < 16; b++)
                for (var p = 1; p < 16; p++)
                    blocks[c * 256 + b * 16 + p] = 0;

        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;
        var iLapMeanLum = 0;
        var iLapMeanChr = 0;

        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var componentCbphp = cbphpPerComponent[c];
            for (var i = 0; i < 16; i++)
            {
                if ((componentCbphp & (1 << i)) == 0) continue;
                var iBlockMap = (int)HierScanOrder[i];
                var n = DecodeBlock(ref reader, state, scan, bChroma, blocks.Slice(c * 256 + iBlockMap * 16, 16));
                if (bChroma) iLapMeanChr += n;
                else iLapMeanLum += n;
            }
        }

        CoefficientModel.Update(
            ref state.Model,
            iLapMean0: iLapMeanLum,
            iLapMean1: iLapMeanChr,
            CoefficientModel.Band.Hp,
            format,
            numComponents);
    }

    /// <summary>
    /// Reject YUV420/YUV422 formats — these have reduced chroma block counts
    /// (1 or 2 blocks per chroma component) and need a separate code path
    /// that this method doesn't implement. Luma-only or full-size-chroma
    /// only for now.
    /// </summary>
    /// <summary>
    /// Compute the per-component CBPHP bitmap for an MB without writing any
    /// bits. The result matches what <see cref="EncodeMb"/> would have
    /// written into its <c>cbphpOut</c> argument and is suitable for feeding
    /// to <see cref="MbCbphp.EncodeMb"/> (which must run BEFORE
    /// <see cref="EncodeMb"/> in the codestream, per T.832 §8.7.16).
    /// </summary>
    /// <param name="blocks"><c>numComponents × 256</c> ints (16 4×4 blocks per component, position 0 unused).</param>
    /// <param name="cbphpOut">Per-component CBPHP bitmaps. Bit <c>i</c> is set if block
    /// <c>HierScanOrder[i]</c> of that component has any non-zero coefficient in positions 1..15.</param>
    public static void ComputeCbphp(int numComponents, ReadOnlySpan<int> blocks, Span<int> cbphpOut)
    {
        if (blocks.Length < numComponents * 256)
            throw new ArgumentException($"blocks must hold {numComponents} × 256 ints", nameof(blocks));
        if (cbphpOut.Length < numComponents)
            throw new ArgumentException($"cbphpOut must hold ≥ {numComponents} ints", nameof(cbphpOut));

        for (var c = 0; c < numComponents; c++)
        {
            var componentBlocks = blocks.Slice(c * 256, 256);
            var componentCbphp = 0;
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                var blockSpan = componentBlocks.Slice(iBlockMap * 16, 16);
                for (var p = 1; p < 16; p++)
                {
                    if (blockSpan[p] != 0)
                    {
                        componentCbphp |= 1 << i;
                        break;
                    }
                }
            }
            cbphpOut[c] = componentCbphp;
        }
    }

    private static void EnsureFullSizeChroma(JxrInternalColorFormat format)
    {
        if (format == JxrInternalColorFormat.YUV420 || format == JxrInternalColorFormat.YUV422)
            throw new NotSupportedException(
                "MbHp.EncodeMb / DecodeMb does not yet handle YUV420 or YUV422 chroma subsampling; " +
                "use the dedicated YUV420/YUV422 path (not yet implemented) for those formats.");
    }

    // -----------------------------------------------------------------------
    // Snapshot/restore wrappers around BlockAdaptive
    //
    // BlockCodingContext is a value struct (intentionally — see the file
    // comment on its declaration), so passing it by ref into BlockAdaptive
    // gives us a single mutating slot. We rebuild it per-block from MbHpState
    // and write back the 5 mutated fields. AbsLevel0/1 are shared across all
    // (Lum and Chr) blocks in the MB; the write-back ensures their deltaDisc
    // accumulation reaches subsequent block calls.
    // -----------------------------------------------------------------------

    private static int EncodeBlock(BitWriter writer, MbHpState state, AdaptiveScan scan, bool bChroma, ReadOnlySpan<int> block)
    {
        var ctx = MakeBlockCtx(state, bChroma);
        var n = BlockAdaptive.Encode(writer, ref ctx, scan, block);
        WriteBackCtx(state, bChroma, ref ctx);
        return n;
    }

    private static int DecodeBlock(ref BitReader reader, MbHpState state, AdaptiveScan scan, bool bChroma, Span<int> block)
    {
        var ctx = MakeBlockCtx(state, bChroma);
        var n = BlockAdaptive.Decode(ref reader, ref ctx, scan, block);
        WriteBackCtx(state, bChroma, ref ctx);
        return n;
    }

    private static BlockCodingContext MakeBlockCtx(MbHpState state, bool bChroma) => new()
    {
        FirstIndex = bChroma ? state.FirstIndexChr : state.FirstIndexLum,
        Index0 = bChroma ? state.IndexChr0 : state.IndexLum0,
        Index1 = bChroma ? state.IndexChr1 : state.IndexLum1,
        AbsLevel0 = state.AbsLevel0,
        AbsLevel1 = state.AbsLevel1,
    };

    private static void WriteBackCtx(MbHpState state, bool bChroma, ref BlockCodingContext ctx)
    {
        if (bChroma)
        {
            state.FirstIndexChr = ctx.FirstIndex;
            state.IndexChr0 = ctx.Index0;
            state.IndexChr1 = ctx.Index1;
        }
        else
        {
            state.FirstIndexLum = ctx.FirstIndex;
            state.IndexLum0 = ctx.Index0;
            state.IndexLum1 = ctx.Index1;
        }
        // AbsLevel states are shared — write back regardless of channel.
        state.AbsLevel0 = ctx.AbsLevel0;
        state.AbsLevel1 = ctx.AbsLevel1;
    }
}
