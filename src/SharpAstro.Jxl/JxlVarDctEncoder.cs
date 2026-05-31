namespace SharpAstro.Jxl;

/// <summary>
/// Assembles a complete lossy VarDCT JPEG XL codestream (ISO/IEC 18181-1) for the minimal DCT8 path —
/// the capstone that wraps the validated payload (<see cref="JxlVarDctImage"/>) and bitstream sections
/// (<see cref="JxlLfGlobalVarDct"/>, <see cref="JxlModularSubimage"/>, <see cref="JxlLfGroup"/>,
/// <see cref="JxlHfGlobal"/>, <see cref="JxlHfCoeff"/>) into a single-group frame libjxl can decode.
///
/// <para>Frame structure (single group, single pass ⇒ one TOC section holding the whole body):</para>
/// <code>
/// FF 0A · SizeHeader · ImageMetadata(all_default ⇒ xyb, 8-bit, sRGB) · FrameHeader(VarDct) · TOC(1)
///   └─ LfGlobal:  LfChannelDequantization(default) · LfGlobalVarDct · GlobalModular(tree only)
///      LfGroup:   LfCoeff(extra_precision + LF-DC) · HfMetadata(blocks + CfL + sharpness)
///      HfGlobal:  DequantMatrixSet(default) · num_hf_presets · HfPass(natural orders + hf_dist cfg)
///      PassGroup: HF coefficient data
/// </code>
///
/// <para>
/// Requires width and height to be multiples of 8 and ≤ 2048 (one LF group). Up to a single 256-px
/// group it emits a single-entry TOC (everything bit-contiguous); larger images get a multi-entry TOC
/// with one byte-aligned PassGroup section per 256×256 region (the HF coefficients are split per group,
/// which is safe because a varblock never crosses a 256-px border). XYB + the default opsin matrix come
/// from the all-default ImageMetadata; chroma is full-resolution; Gabor/EPF restoration is disabled so
/// the reconstruction is pure inverse-DCT. The two entropy distributions are split per the 5i config/
/// data discipline: the Modular sample config rides in GlobalModular's tree and its data in LfGroup; the
/// hf_dist config rides in HfGlobal and its symbols are split across the PassGroups.
/// </para>
/// </summary>
internal static class JxlVarDctEncoder
{
    /// <summary>Blocks per group edge — VarDCT fixes the group dim at 256 px = 32 blocks.</summary>
    private const int GroupBlocks = 32;

    /// <summary>
    /// Encode 8-bit RGB (<paramref name="rgb"/> = three row-major <c>w·h</c> planes, values 0..255) as a
    /// lossy VarDCT .jxl. The quantizer knobs default to a high-quality setting.
    /// </summary>
    public static byte[] EncodeRgb24(
        int[][] rgb, int width, int height,
        int globalScale = 4096, int quantLf = 32, int hfMul = 32, int extraPrecision = 0)
    {
        if (rgb.Length != 3)
            throw new ArgumentException("Expected 3 RGB channels.", nameof(rgb));
        if (width % 8 != 0 || height % 8 != 0)
            throw new NotSupportedException("JPEG XL VarDCT encoder: width/height must be multiples of 8.");
        // ≤ 2048 keeps the whole image inside one LF group (lf_group_dim = 2048); multiple PassGroups
        // (one per 256×256 region) are produced for anything larger than a single 256-px group.
        if (width > 2048 || height > 2048)
            throw new NotSupportedException("JPEG XL VarDCT encoder: images larger than 2048 (multi-LF-group) are not yet supported.");

        var srgb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            srgb[c] = new float[width * height];
            for (int i = 0; i < width * height; i++)
                srgb[c][i] = rgb[c][i] / 255f;
        }

        JxlVarDctImage.Encoded enc = JxlVarDctImage.Encode(srgb, width, height, globalScale, quantLf, hfMul, extraPrecision);
        int bw = enc.Bw, bh = enc.Bh;

        // Block grid: every cell is a Dct8 data block with the shared hf_mul.
        var blockGrid = new JxlBlockInfo[bw * bh];
        for (int i = 0; i < blockGrid.Length; i++)
            blockGrid[i] = JxlBlockInfo.Data(JxlVarDctTransform.Dct8, hfMul);

        // LfGroup (one, covering the whole ≤2048 image): LF-DC in modular channel order [Y, X, B];
        // CfL grids zero (default kx=0, kb=1).
        int cfW = CeilDiv(width, 64), cfH = CeilDiv(height, 64);
        var lfGroup = new JxlLfGroup
        {
            Bw = bw, Bh = bh, ExtraPrecision = extraPrecision,
            LfQuant = [enc.LfQuant[1], enc.LfQuant[0], enc.LfQuant[2]],
            BlockGrid = blockGrid,
            CfW = cfW, CfH = cfH,
            XFromY = new int[cfW * cfH], BFromY = new int[cfW * cfH],
        };
        (List<(int Ctx, uint Value)> lfStream, List<(int Ctx, uint Value)> hfMetaStream) = lfGroup.BuildStreams();
        JxlEntropyEncoder sampleEnc = JxlModularSubimage.NewSampleEncoder();
        JxlEntropyEncoder.Plan samplePlan = sampleEnc.Prepare(lfStream, hfMetaStream);

        // HF coefficients, split into one symbol stream per 32×32-block PassGroup region. A varblock
        // never crosses a 256-px border, so each group's coefficients are self-contained.
        int gpr = CeilDiv(bw, GroupBlocks), gpc = CeilDiv(bh, GroupBlocks);
        int numGroups = gpr * gpc;
        var hfStreams = new List<(int Ctx, uint Value)>[numGroups];
        for (int g = 0; g < numGroups; g++)
            hfStreams[g] = BuildGroupHfStream(enc, blockGrid, bw, gpr, hfMul, g);

        const int ctxSize = 495 * 15;
        var hfDistEnc = new JxlEntropyEncoder(new byte[ctxSize], [JxlIntegerConfig.Create(4, 0, 0)]);
        JxlEntropyEncoder.Plan hfDistPlan = hfDistEnc.Prepare(hfStreams); // one shared distribution over all groups

        var bwr = new JxlBitWriter();
        bwr.WriteBits(0xFF, 8);
        bwr.WriteBits(0x0A, 8); // codestream signature FF 0A
        WriteSizeHeader(bwr, width, height);
        bwr.WriteBit(true); // ImageMetadata all_default => xyb_encoded, 8-bit, sRGB
        bwr.WriteBit(true); // default_m = true (read unconditionally, even when all_default) => default opsin matrix
        WriteFrameHeader(bwr);

        if (numGroups == 1)
        {
            // num_groups == 1 && num_passes == 1: a single TOC entry holds the whole body bit-contiguously.
            var sec = new JxlBitWriter();
            WriteLfGlobalPart(sec, globalScale, quantLf, sampleEnc, samplePlan);
            lfGroup.Write(sec, sampleEnc, samplePlan, lfStream, hfMetaStream);
            JxlHfGlobal.Write(sec, numGroups: 1, numHfPresets: 1, hfDistEnc, hfDistPlan);
            WritePassGroupPart(sec, hfDistEnc, hfDistPlan, hfStreams[0], numHfPresets: 1);
            byte[] body = sec.ToArray();
            WriteToc(bwr, body.Length);
            bwr.WriteBytes(body);
        }
        else
        {
            // Multi-entry TOC: LfGlobal, LfGroup(s), HfGlobal, then one PassGroup per group — each a
            // separate byte-aligned section.
            var lfGlobalSec = new JxlBitWriter();
            WriteLfGlobalPart(lfGlobalSec, globalScale, quantLf, sampleEnc, samplePlan);
            var lfGroupSec = new JxlBitWriter();
            lfGroup.Write(lfGroupSec, sampleEnc, samplePlan, lfStream, hfMetaStream);
            var hfGlobalSec = new JxlBitWriter();
            JxlHfGlobal.Write(hfGlobalSec, numGroups, numHfPresets: 1, hfDistEnc, hfDistPlan);

            var sections = new List<byte[]> { lfGlobalSec.ToArray(), lfGroupSec.ToArray(), hfGlobalSec.ToArray() };
            for (int g = 0; g < numGroups; g++)
            {
                var passSec = new JxlBitWriter();
                WritePassGroupPart(passSec, hfDistEnc, hfDistPlan, hfStreams[g], numHfPresets: 1);
                sections.Add(passSec.ToArray());
            }

            WriteTocMulti(bwr, sections.Select(s => s.Length).ToArray());
            foreach (byte[] s in sections)
                bwr.WriteBytes(s);
        }

        return bwr.ToArray();
    }

    /// <summary>Build the HF symbol stream for PassGroup <paramref name="g"/> (a ≤32×32-block region).</summary>
    private static List<(int Ctx, uint Value)> BuildGroupHfStream(
        JxlVarDctImage.Encoded enc, JxlBlockInfo[] blockGrid, int bw, int gpr, int hfMul, int g)
    {
        int gcol = g % gpr, grow = g / gpr;
        int bx0 = gcol * GroupBlocks, by0 = grow * GroupBlocks;
        int bwG = Math.Min(GroupBlocks, bw - bx0);
        int bhG = Math.Min(GroupBlocks, enc.Bh - by0);

        var subGrid = new JxlBlockInfo[bwG * bhG];
        for (int y = 0; y < bhG; y++)
            for (int x = 0; x < bwG; x++)
                subGrid[y * bwG + x] = blockGrid[(by0 + y) * bw + (bx0 + x)];

        int gridW = bw * 8, subW = bwG * 8;
        var subCoeff = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            subCoeff[c] = new int[subW * (bhG * 8)];
            for (int py = 0; py < bhG * 8; py++)
                for (int px = 0; px < bwG * 8; px++)
                    subCoeff[c][py * subW + px] = enc.HfQuant[c][(by0 * 8 + py) * gridW + bx0 * 8 + px];
        }

        var p = new JxlHfCoeff.Params
        {
            Bw = bwG, Bh = bhG, BlockGrid = subGrid,
            NumBlockClusters = 15, BlockCtxMap = JxlLfGlobalVarDct.DefaultBlockCtxMap,
        };
        return JxlHfCoeff.Encode(p, subCoeff);
    }

    private static void WriteLfGlobalPart(
        JxlBitWriter bw, int globalScale, int quantLf, JxlEntropyEncoder sampleEnc, JxlEntropyEncoder.Plan samplePlan)
    {
        // LfGlobal: no patches/splines/noise (frame flags = 0); LfChannelDequantization all_default.
        bw.WriteBit(true); // LfChannelDequantization all_default
        new JxlLfGlobalVarDct { GlobalScale = globalScale, QuantLf = quantLf }.Write(bw);

        // GlobalModular: a global tree (used by the LfGroup sub-images) and no colour channels, so the
        // empty stream-0 modular carries no header at all (jxl-modular Modular::parse short-circuits).
        bw.WriteBit(true); // global_tree_present
        JxlModularSubimage.WriteSharedTree(bw, sampleEnc, samplePlan);
    }

    private static void WritePassGroupPart(
        JxlBitWriter bw, JxlEntropyEncoder hfDistEnc, JxlEntropyEncoder.Plan hfDistPlan,
        List<(int Ctx, uint Value)> hfStream, int numHfPresets)
    {
        int hfpBits = JxlHfGlobal.CeilLog2(numHfPresets);
        if (hfpBits > 0)
            bw.WriteBits(0, hfpBits); // select HF preset 0
        hfDistEnc.WriteData(bw, hfDistPlan, hfStream);
    }

    // ---- codestream header chain (VarDct variants of the JxlModularEncoder writers) ----

    private static void WriteSizeHeader(JxlBitWriter bw, int width, int height)
    {
        bw.WriteBit(false); // div8 = false -> general dimension coding
        WriteDimension(bw, height);
        bw.WriteBits(0, 3); // ratio = 0 -> explicit width
        WriteDimension(bw, width);
    }

    private static void WriteDimension(JxlBitWriter bw, int value) =>
        bw.WriteU32((uint)(value - 1), (0, 9), (0, 13), (0, 18), (0, 30));

    private static void WriteFrameHeader(JxlBitWriter bw)
    {
        bw.ZeroPadToByte();
        bw.WriteBit(false);                              // all_default = false
        bw.WriteBits((uint)JxlFrameType.RegularFrame, 2);
        bw.WriteBits((uint)JxlFrameEncoding.VarDct, 1);  // encoding = VarDct
        bw.WriteU64(0);                                  // flags
        // do_ycbcr: absent when xyb_encoded (the reader short-circuits the bit away).
        bw.WriteU32(1, (1, 0), (2, 0), (4, 0), (8, 0));  // upsampling = 1
        // group_size_shift: absent for VarDct (group dim fixed at 256).
        bw.WriteBits(2, 3);                              // x_qm_scale = 2 -> qm_scale_X = 0.8^0 = 1
        bw.WriteBits(2, 3);                              // b_qm_scale = 2 -> qm_scale_B = 1
        bw.WriteU32(1, (1, 0), (2, 0), (3, 0), (4, 3));  // num_passes = 1
        bw.WriteBit(false);                              // have_crop = false
        bw.WriteU32(0, (0, 0), (1, 0), (2, 0), (3, 2));  // blend mode = Replace
        bw.WriteBit(true);                               // is_last = true
        bw.WriteU32(0, (0, 0), (0, 4), (16, 5), (48, 10)); // name length = 0

        // Restoration filter disabled (Gabor + EPF are lossy smoothing; our decode applies neither).
        bw.WriteBit(false); // restoration filter all_default = false
        bw.WriteBit(false); // gab_enabled = false
        bw.WriteBits(0, 2); // epf_iters = 0
        bw.WriteU64(0);     // restoration-filter extensions (none)

        bw.WriteU64(0);     // frame-header extensions (none)
    }

    private static void WriteToc(JxlBitWriter bw, int sectionLength) => WriteTocMulti(bw, [sectionLength]);

    private static void WriteTocMulti(JxlBitWriter bw, int[] sectionLengths)
    {
        bw.WriteBit(false); // permuted = false
        bw.ZeroPadToByte();
        foreach (int len in sectionLengths)
            bw.WriteU32((uint)len, (0, 10), (1024, 14), (17408, 22), (4211712, 30));
        bw.ZeroPadToByte();
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
