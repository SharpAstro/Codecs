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
    // Phase 21b: iModelBits split + inline FlexBits emission.
    //
    // When iModelBits > 0, each HP coefficient is split per T.832 §9.5.4
    // HPBlockCoefficientRemap:
    //
    //   absV     = |trueValue|
    //   vlcAbs   = absV >> iModelBits                       (encoded by VLC pass)
    //   flexAbs  = (absV >> trimFlexBits) & ((1 << iFlexBitsLeft) - 1)
    //                                                       (encoded by BlockFlexBits)
    //   iFlexBitsLeft = max(0, iModelBits - trimFlexBits)
    //
    // Reconstruction:
    //
    //   absRecon = (vlcAbs << iModelBits) | (flexAbs << trimFlexBits)
    //
    // Bits in [0, trimFlexBits) are dropped (lossy trim). Sign is carried by
    // the VLC when vlcAbs > 0, otherwise by an explicit SIGN_FLAG in
    // BlockFlexBits (only emitted when |flexAbs| > 0).
    //
    // For SPATIAL mode (MB_HP_FLEX inlined immediately after HP VLC of the
    // same MB) we write the FlexBits to the same BitWriter. FREQUENCY mode's
    // separate TILE_FLEXBITS substream will need a different overload that
    // takes a second writer; that's Phase 21d.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Split a signed coefficient into the VLC value (high bits, signed) and
    /// the signed refinement (low bits, signed in the BlockFlexBits sense).
    /// </summary>
    private static void SplitSignedCoefficient(
        int signedV, int iModelBits, int trimFlexBits, int iFlexBitsLeft,
        out int vlcValue, out int signedRefinement)
    {
        if (iModelBits == 0)
        {
            vlcValue = signedV;
            signedRefinement = 0;
            return;
        }
        var absV = signedV < 0 ? -signedV : signedV;
        var vlcAbs = absV >> iModelBits;
        var flexAbs = iFlexBitsLeft > 0
            ? (absV >> trimFlexBits) & ((1 << iFlexBitsLeft) - 1)
            : 0;
        vlcValue = signedV < 0 ? -vlcAbs : vlcAbs;
        // When vlcAbs > 0 the sign is implicit (carried by VLC); the
        // refinement uses the same sign so |refinement| recovers cleanly.
        // When vlcAbs == 0 and flexAbs != 0 the refinement carries the sign
        // (BlockFlexBits emits a SIGN_FLAG); we encode this by signing the
        // refinement with the original value's sign.
        signedRefinement = signedV < 0 ? -flexAbs : flexAbs;
    }

    /// <summary>Reconstruct a full coefficient from the VLC value + signed refinement.</summary>
    private static int ReconstructCoefficient(int vlcValue, int signedRefinement, int iModelBits, int trimFlexBits)
    {
        if (iModelBits == 0) return vlcValue;
        var absVlc = vlcValue < 0 ? -vlcValue : vlcValue;
        var absRef = signedRefinement < 0 ? -signedRefinement : signedRefinement;
        var absRecon = (absVlc << iModelBits) | (absRef << trimFlexBits);
        var negative = vlcValue != 0 ? vlcValue < 0 : signedRefinement < 0;
        return negative ? -absRecon : absRecon;
    }

    /// <summary>
    /// Encode a Luma-only MB with the FlexBits split applied — VLC pass uses
    /// <c>state.Model.MBits0</c> as iModelBits, the refinement bits land in
    /// the same <paramref name="writer"/> immediately after the VLC pass
    /// (SPATIAL mode layout).
    /// </summary>
    public static int EncodeLumaMb(
        BitWriter writer,
        MbHpState state,
        int mbhpMode,
        int trimFlexBits,
        ReadOnlySpan<int> blocks)
        => EncodeLumaMb(writer, writer, state, mbhpMode, trimFlexBits, blocks);

    /// <summary>
    /// FREQUENCY-mode variant: VLC bits go to <paramref name="vlcWriter"/>,
    /// FlexBits refinement bits go to <paramref name="flexWriter"/>. Pass the
    /// same writer twice for SPATIAL inline layout.
    /// </summary>
    public static int EncodeLumaMb(
        BitWriter vlcWriter,
        BitWriter flexWriter,
        MbHpState state,
        int mbhpMode,
        int trimFlexBits,
        ReadOnlySpan<int> blocks)
    {
        if (blocks.Length < 256)
            throw new ArgumentException("blocks must hold 16 blocks * 16 positions = 256 ints", nameof(blocks));

        var iModelBits = state.Model.MBits0;
        var iFlexBitsLeft = Math.Max(0, iModelBits - trimFlexBits);
        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;

        // Heap-allocated to satisfy ref-safety rules — ref BitReader / BitWriter
        // alongside stackalloc spans tripped CS8350.
        var vlc = new int[256];
        var refs = new int[240]; // 16 blocks * 15 AC positions
        var cbphp = 0;

        // Pass 1 — split every AC coefficient and compute CBPHP from VLC values.
        for (var i = 0; i < 16; i++)
        {
            var iBlockMap = (int)HierScanOrder[i];
            var blockSpan = blocks.Slice(iBlockMap * 16, 16);
            var hasNonZero = false;
            for (var p = 1; p < 16; p++)
            {
                SplitSignedCoefficient(blockSpan[p], iModelBits, trimFlexBits, iFlexBitsLeft,
                    out var vlcV, out var refV);
                vlc[iBlockMap * 16 + p] = vlcV;
                refs[iBlockMap * 15 + (p - 1)] = refV;
                if (vlcV != 0) hasNonZero = true;
            }
            if (hasNonZero)
                cbphp |= 1 << i;
        }

        // Pass 2 — VLC for blocks the CBPHP bit is set on, in hierarchical order.
        var totalNonZero = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((cbphp & (1 << i)) == 0) continue;
            var iBlockMap = (int)HierScanOrder[i];
            totalNonZero += EncodeBlock(vlcWriter, state, scan, bChroma: false,
                vlc.AsSpan(iBlockMap * 16, 16));
        }

        // Pass 3 — FlexBits for every block (regardless of CBPHP) when iModelBits > 0.
        if (iModelBits > 0)
        {
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                BlockFlexBits.EncodeBlock(flexWriter,
                    vlcBlock: vlc.AsSpan(iBlockMap * 16 + 1, 15),
                    refBlock: refs.AsSpan(iBlockMap * 15, 15),
                    iFlexBitsLeft);
            }
        }

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
    /// Decode a Luma-only MB with the FlexBits split applied — inverse of the
    /// new <see cref="EncodeLumaMb(BitWriter, MbHpState, int, int, ReadOnlySpan{int})"/>.
    /// </summary>
    public static void DecodeLumaMb(
        ref BitReader reader,
        MbHpState state,
        int mbhpMode,
        int trimFlexBits,
        int cbphp,
        Span<int> blocks)
    {
        if (blocks.Length < 256)
            throw new ArgumentException("blocks must hold 16 blocks * 16 positions = 256 ints", nameof(blocks));

        var iModelBits = state.Model.MBits0;
        var iFlexBitsLeft = Math.Max(0, iModelBits - trimFlexBits);
        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;

        var vlc = new int[256];
        var refs = new int[240];

        // Pass 1 — VLC for blocks the CBPHP bit is set on. Others stay at vlc[..]=0.
        var totalNonZero = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((cbphp & (1 << i)) == 0) continue;
            var iBlockMap = (int)HierScanOrder[i];
            totalNonZero += DecodeBlock(ref reader, state, scan, bChroma: false,
                vlc.AsSpan(iBlockMap * 16, 16));
        }

        // Pass 2 — FlexBits for every block when iModelBits > 0.
        if (iModelBits > 0)
        {
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                BlockFlexBits.DecodeBlock(ref reader,
                    vlcBlock: vlc.AsSpan(iBlockMap * 16 + 1, 15),
                    refBlock: refs.AsSpan(iBlockMap * 15, 15),
                    iFlexBitsLeft);
            }
        }

        // Pass 3 — reconstruct full coefficients into the output. Position 0
        // of each block is left untouched (DC is the caller's responsibility).
        for (var iBlockMap = 0; iBlockMap < 16; iBlockMap++)
        {
            for (var p = 1; p < 16; p++)
            {
                var vlcV = vlc[iBlockMap * 16 + p];
                var refV = refs[iBlockMap * 15 + (p - 1)];
                blocks[iBlockMap * 16 + p] = ReconstructCoefficient(vlcV, refV, iModelBits, trimFlexBits);
            }
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
    /// Multi-component EncodeMb with FlexBits split — luma uses
    /// <c>state.Model.MBits0</c>, chroma components 1..N-1 use
    /// <c>state.Model.MBits1</c>. SPATIAL-mode layout: FlexBits inlined to
    /// the same writer after the VLC pass.
    /// </summary>
    public static void EncodeMb(
        BitWriter writer,
        MbHpState state,
        int mbhpMode,
        JxrInternalColorFormat format,
        int numComponents,
        int trimFlexBits,
        ReadOnlySpan<int> blocks,
        Span<int> cbphpOut)
        => EncodeMb(writer, writer, state, mbhpMode, format, numComponents, trimFlexBits, blocks, cbphpOut);

    /// <summary>
    /// FREQUENCY-mode multi-component encode: VLC bits go to
    /// <paramref name="vlcWriter"/>, FlexBits to <paramref name="flexWriter"/>.
    /// Pass the same writer twice for SPATIAL inline layout.
    /// </summary>
    public static void EncodeMb(
        BitWriter vlcWriter,
        BitWriter flexWriter,
        MbHpState state,
        int mbhpMode,
        JxrInternalColorFormat format,
        int numComponents,
        int trimFlexBits,
        ReadOnlySpan<int> blocks,
        Span<int> cbphpOut)
    {
        EnsureFullSizeChroma(format);
        if (blocks.Length < numComponents * 256)
            throw new ArgumentException($"blocks must hold {numComponents} components × 256 ints", nameof(blocks));
        if (cbphpOut.Length < numComponents)
            throw new ArgumentException($"cbphpOut must hold {numComponents} ints", nameof(cbphpOut));

        var iModelBitsLum = state.Model.MBits0;
        var iModelBitsChr = state.Model.MBits1;
        var iFlexBitsLeftLum = Math.Max(0, iModelBitsLum - trimFlexBits);
        var iFlexBitsLeftChr = Math.Max(0, iModelBitsChr - trimFlexBits);
        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;

        var totalVlc = blocks.Length;
        var vlc = new int[totalVlc];
        var refs = new int[numComponents * 240];

        // Pass 1 — split + CBPHP per component.
        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var iModelBits = bChroma ? iModelBitsChr : iModelBitsLum;
            var iFlexBitsLeft = bChroma ? iFlexBitsLeftChr : iFlexBitsLeftLum;
            var componentBlocks = blocks.Slice(c * 256, 256);
            var componentCbphp = 0;
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                var blockSpan = componentBlocks.Slice(iBlockMap * 16, 16);
                var hasNonZero = false;
                for (var p = 1; p < 16; p++)
                {
                    SplitSignedCoefficient(blockSpan[p], iModelBits, trimFlexBits, iFlexBitsLeft,
                        out var vlcV, out var refV);
                    vlc[c * 256 + iBlockMap * 16 + p] = vlcV;
                    refs[c * 240 + iBlockMap * 15 + (p - 1)] = refV;
                    if (vlcV != 0) hasNonZero = true;
                }
                if (hasNonZero)
                    componentCbphp |= 1 << i;
            }
            cbphpOut[c] = componentCbphp;
        }

        // Pass 2 — VLC writes, components in order, blocks in hierarchical order.
        var iLapMeanLum = 0;
        var iLapMeanChr = 0;
        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var componentCbphp = cbphpOut[c];
            for (var i = 0; i < 16; i++)
            {
                if ((componentCbphp & (1 << i)) == 0) continue;
                var iBlockMap = (int)HierScanOrder[i];
                var n = EncodeBlock(vlcWriter, state, scan, bChroma,
                    vlc.AsSpan(c * 256 + iBlockMap * 16, 16));
                if (bChroma) iLapMeanChr += n;
                else iLapMeanLum += n;
            }
        }

        // Pass 3 — FlexBits per component when its iModelBits > 0.
        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var iModelBits = bChroma ? iModelBitsChr : iModelBitsLum;
            if (iModelBits == 0) continue;
            var iFlexBitsLeft = bChroma ? iFlexBitsLeftChr : iFlexBitsLeftLum;
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                BlockFlexBits.EncodeBlock(flexWriter,
                    vlcBlock: vlc.AsSpan(c * 256 + iBlockMap * 16 + 1, 15),
                    refBlock: refs.AsSpan(c * 240 + iBlockMap * 15, 15),
                    iFlexBitsLeft);
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
    /// Multi-component DecodeMb with FlexBits split — dual of the new
    /// <see cref="EncodeMb(BitWriter, MbHpState, int, JxrInternalColorFormat, int, int, ReadOnlySpan{int}, Span{int})"/>.
    /// </summary>
    public static void DecodeMb(
        ref BitReader reader,
        MbHpState state,
        int mbhpMode,
        JxrInternalColorFormat format,
        int numComponents,
        int trimFlexBits,
        ReadOnlySpan<int> cbphpPerComponent,
        Span<int> blocks)
    {
        EnsureFullSizeChroma(format);
        if (blocks.Length < numComponents * 256)
            throw new ArgumentException($"blocks must hold {numComponents} components × 256 ints", nameof(blocks));
        if (cbphpPerComponent.Length < numComponents)
            throw new ArgumentException($"cbphpPerComponent must hold {numComponents} ints", nameof(cbphpPerComponent));

        var iModelBitsLum = state.Model.MBits0;
        var iModelBitsChr = state.Model.MBits1;
        var iFlexBitsLeftLum = Math.Max(0, iModelBitsLum - trimFlexBits);
        var iFlexBitsLeftChr = Math.Max(0, iModelBitsChr - trimFlexBits);
        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;

        var vlc = new int[numComponents * 256];
        var refs = new int[numComponents * 240];

        // Pass 1 — VLC reads, in the same component-then-hierarchical order as encode.
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
                var n = DecodeBlock(ref reader, state, scan, bChroma,
                    vlc.AsSpan(c * 256 + iBlockMap * 16, 16));
                if (bChroma) iLapMeanChr += n;
                else iLapMeanLum += n;
            }
        }

        // Pass 2 — FlexBits reads per component when its iModelBits > 0.
        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var iModelBits = bChroma ? iModelBitsChr : iModelBitsLum;
            if (iModelBits == 0) continue;
            var iFlexBitsLeft = bChroma ? iFlexBitsLeftChr : iFlexBitsLeftLum;
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                BlockFlexBits.DecodeBlock(ref reader,
                    vlcBlock: vlc.AsSpan(c * 256 + iBlockMap * 16 + 1, 15),
                    refBlock: refs.AsSpan(c * 240 + iBlockMap * 15, 15),
                    iFlexBitsLeft);
            }
        }

        // Pass 3 — reconstruct full coefficients.
        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var iModelBits = bChroma ? iModelBitsChr : iModelBitsLum;
            for (var iBlockMap = 0; iBlockMap < 16; iBlockMap++)
            {
                for (var p = 1; p < 16; p++)
                {
                    var vlcV = vlc[c * 256 + iBlockMap * 16 + p];
                    var refV = refs[c * 240 + iBlockMap * 15 + (p - 1)];
                    blocks[c * 256 + iBlockMap * 16 + p] = ReconstructCoefficient(vlcV, refV, iModelBits, trimFlexBits);
                }
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
    /// FREQUENCY-mode DecodeLumaMb variant: VLC bits come from
    /// <paramref name="vlcReader"/>, FlexBits bits come from
    /// <paramref name="flexReader"/>. The SPATIAL inline variant lives
    /// alongside (single reader); these two cannot be expressed in terms
    /// of each other because <see cref="BitReader"/> is a ref struct and
    /// passing the same ref twice would alias.
    /// </summary>
    public static void DecodeLumaMb(
        ref BitReader vlcReader,
        ref BitReader flexReader,
        MbHpState state,
        int mbhpMode,
        int trimFlexBits,
        int cbphp,
        Span<int> blocks)
    {
        if (blocks.Length < 256)
            throw new ArgumentException("blocks must hold 16 blocks * 16 positions = 256 ints", nameof(blocks));

        var iModelBits = state.Model.MBits0;
        var iFlexBitsLeft = Math.Max(0, iModelBits - trimFlexBits);
        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;

        var vlc = new int[256];
        var refs = new int[240];

        var totalNonZero = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((cbphp & (1 << i)) == 0) continue;
            var iBlockMap = (int)HierScanOrder[i];
            totalNonZero += DecodeBlock(ref vlcReader, state, scan, bChroma: false,
                vlc.AsSpan(iBlockMap * 16, 16));
        }

        if (iModelBits > 0)
        {
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                BlockFlexBits.DecodeBlock(ref flexReader,
                    vlcBlock: vlc.AsSpan(iBlockMap * 16 + 1, 15),
                    refBlock: refs.AsSpan(iBlockMap * 15, 15),
                    iFlexBitsLeft);
            }
        }

        for (var iBlockMap = 0; iBlockMap < 16; iBlockMap++)
        {
            for (var p = 1; p < 16; p++)
            {
                var vlcV = vlc[iBlockMap * 16 + p];
                var refV = refs[iBlockMap * 15 + (p - 1)];
                blocks[iBlockMap * 16 + p] = ReconstructCoefficient(vlcV, refV, iModelBits, trimFlexBits);
            }
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
    /// FREQUENCY-mode multi-component DecodeMb variant — see
    /// <see cref="DecodeLumaMb(ref BitReader, ref BitReader, MbHpState, int, int, int, Span{int})"/>.
    /// </summary>
    public static void DecodeMb(
        ref BitReader vlcReader,
        ref BitReader flexReader,
        MbHpState state,
        int mbhpMode,
        JxrInternalColorFormat format,
        int numComponents,
        int trimFlexBits,
        ReadOnlySpan<int> cbphpPerComponent,
        Span<int> blocks)
    {
        EnsureFullSizeChroma(format);
        if (blocks.Length < numComponents * 256)
            throw new ArgumentException($"blocks must hold {numComponents} components × 256 ints", nameof(blocks));
        if (cbphpPerComponent.Length < numComponents)
            throw new ArgumentException($"cbphpPerComponent must hold {numComponents} ints", nameof(cbphpPerComponent));

        var iModelBitsLum = state.Model.MBits0;
        var iModelBitsChr = state.Model.MBits1;
        var iFlexBitsLeftLum = Math.Max(0, iModelBitsLum - trimFlexBits);
        var iFlexBitsLeftChr = Math.Max(0, iModelBitsChr - trimFlexBits);
        var scan = mbhpMode == 1 ? state.ScanVertical : state.ScanHorizontal;

        var vlc = new int[numComponents * 256];
        var refs = new int[numComponents * 240];

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
                var n = DecodeBlock(ref vlcReader, state, scan, bChroma,
                    vlc.AsSpan(c * 256 + iBlockMap * 16, 16));
                if (bChroma) iLapMeanChr += n;
                else iLapMeanLum += n;
            }
        }

        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var iModelBits = bChroma ? iModelBitsChr : iModelBitsLum;
            if (iModelBits == 0) continue;
            var iFlexBitsLeft = bChroma ? iFlexBitsLeftChr : iFlexBitsLeftLum;
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                BlockFlexBits.DecodeBlock(ref flexReader,
                    vlcBlock: vlc.AsSpan(c * 256 + iBlockMap * 16 + 1, 15),
                    refBlock: refs.AsSpan(c * 240 + iBlockMap * 15, 15),
                    iFlexBitsLeft);
            }
        }

        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var iModelBits = bChroma ? iModelBitsChr : iModelBitsLum;
            for (var iBlockMap = 0; iBlockMap < 16; iBlockMap++)
            {
                for (var p = 1; p < 16; p++)
                {
                    var vlcV = vlc[c * 256 + iBlockMap * 16 + p];
                    var refV = refs[c * 240 + iBlockMap * 15 + (p - 1)];
                    blocks[c * 256 + iBlockMap * 16 + p] = ReconstructCoefficient(vlcV, refV, iModelBits, trimFlexBits);
                }
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
    /// iModelBits-aware CBPHP computation — bit <c>i</c> set when block
    /// <c>HierScanOrder[i]</c> has any coefficient whose VLC value (after
    /// the <paramref name="iModelBits"/> split) is non-zero.
    /// </summary>
    public static void ComputeCbphpWithSplit(
        int numComponents,
        int iModelBitsLum, int iModelBitsChr,
        ReadOnlySpan<int> blocks,
        Span<int> cbphpOut)
    {
        if (blocks.Length < numComponents * 256)
            throw new ArgumentException($"blocks must hold {numComponents} × 256 ints", nameof(blocks));
        if (cbphpOut.Length < numComponents)
            throw new ArgumentException($"cbphpOut must hold ≥ {numComponents} ints", nameof(cbphpOut));

        for (var c = 0; c < numComponents; c++)
        {
            var iModelBits = c == 0 ? iModelBitsLum : iModelBitsChr;
            var componentBlocks = blocks.Slice(c * 256, 256);
            var componentCbphp = 0;
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                var blockSpan = componentBlocks.Slice(iBlockMap * 16, 16);
                for (var p = 1; p < 16; p++)
                {
                    var absV = blockSpan[p] < 0 ? -blockSpan[p] : blockSpan[p];
                    if ((absV >> iModelBits) != 0)
                    {
                        componentCbphp |= 1 << i;
                        break;
                    }
                }
            }
            cbphpOut[c] = componentCbphp;
        }
    }

    /// <summary>
    /// Two-pass FREQUENCY-mode helper: given an MB whose <paramref name="hpInPlace"/>
    /// already holds the VLC values (from a prior no-flex pass), read this MB's
    /// FlexBits refinements from <paramref name="reader"/> and overwrite
    /// <paramref name="hpInPlace"/> with the fully reconstructed coefficients.
    /// </summary>
    /// <remarks>
    /// iModelBits is supplied explicitly (per MB) rather than read from state,
    /// because the caller has already advanced the <see cref="MbHpState.Model"/>
    /// through all MBs by the time the FlexBits pass runs. The caller is
    /// expected to snapshot per-MB iModelBits during the VLC pass and replay
    /// them here.
    /// </remarks>
    public static void ReadFlexBitsAndReconstruct(
        ref BitReader reader,
        JxrInternalColorFormat format, int numComponents,
        int iModelBitsLum, int iModelBitsChr, int trimFlexBits,
        Span<int> hpInPlace)
    {
        EnsureFullSizeChroma(format);
        if (hpInPlace.Length < numComponents * 256)
            throw new ArgumentException($"hpInPlace must hold {numComponents} × 256 ints", nameof(hpInPlace));

        var iFlexBitsLeftLum = Math.Max(0, iModelBitsLum - trimFlexBits);
        var iFlexBitsLeftChr = Math.Max(0, iModelBitsChr - trimFlexBits);

        // Snapshot the VLC values before they get overwritten — the
        // reconstruction formula needs (vlcSign, vlcAbs) per position.
        var vlc = new int[numComponents * 256];
        for (var i = 0; i < vlc.Length; i++) vlc[i] = hpInPlace[i];
        var refs = new int[numComponents * 240];

        // FlexBits sub-stream layout matches the encoder's pass-3 order: per
        // component (luma then chroma), 16 blocks in HierScanOrder, 15 AC
        // positions in Transpose444 order (handled inside BlockFlexBits).
        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var iModelBits = bChroma ? iModelBitsChr : iModelBitsLum;
            if (iModelBits == 0) continue;
            var iFlexBitsLeft = bChroma ? iFlexBitsLeftChr : iFlexBitsLeftLum;
            for (var i = 0; i < 16; i++)
            {
                var iBlockMap = (int)HierScanOrder[i];
                BlockFlexBits.DecodeBlock(ref reader,
                    vlcBlock: vlc.AsSpan(c * 256 + iBlockMap * 16 + 1, 15),
                    refBlock: refs.AsSpan(c * 240 + iBlockMap * 15, 15),
                    iFlexBitsLeft);
            }
        }

        for (var c = 0; c < numComponents; c++)
        {
            var bChroma = c > 0;
            var iModelBits = bChroma ? iModelBitsChr : iModelBitsLum;
            for (var iBlockMap = 0; iBlockMap < 16; iBlockMap++)
            {
                for (var p = 1; p < 16; p++)
                {
                    var vlcV = vlc[c * 256 + iBlockMap * 16 + p];
                    var refV = refs[c * 240 + iBlockMap * 15 + (p - 1)];
                    hpInPlace[c * 256 + iBlockMap * 16 + p] = ReconstructCoefficient(vlcV, refV, iModelBits, trimFlexBits);
                }
            }
        }
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
