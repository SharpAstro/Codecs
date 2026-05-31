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
/// Requires width and height to be multiples of 8 and ≤ 256 (VarDCT fixes the group dim at 256, so this
/// stays a single group / single LF group). XYB + the default opsin matrix come from the all-default
/// ImageMetadata; chroma is full-resolution; Gabor/EPF restoration is disabled so the reconstruction is
/// pure inverse-DCT (matching <see cref="JxlVarDctImage.Decode"/>). The two entropy distributions are
/// split per the 5i config/data discipline: the Modular sample config rides in GlobalModular's tree and
/// its data in LfGroup; the hf_dist config rides in HfGlobal and its symbols in the PassGroup.
/// </para>
/// </summary>
internal static class JxlVarDctEncoder
{
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
        if (width > 256 || height > 256)
            throw new NotSupportedException("JPEG XL VarDCT encoder: only single-group images (≤ 256) are supported.");

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

        // LfGroup: LF-DC in modular channel order [Y, X, B]; CfL grids zero (default kx=0, kb=1).
        int cfW = CeilDiv(width, 64), cfH = CeilDiv(height, 64);
        var lfGroup = new JxlLfGroup
        {
            Bw = bw, Bh = bh, ExtraPrecision = extraPrecision,
            LfQuant = [enc.LfQuant[1], enc.LfQuant[0], enc.LfQuant[2]],
            BlockGrid = blockGrid,
            CfW = cfW, CfH = cfH,
            XFromY = new int[cfW * cfH], BFromY = new int[cfW * cfH],
        };

        // HF coefficient symbol stream (physical channel order [X, Y, B], DC positions 0).
        var hfParams = new JxlHfCoeff.Params
        {
            Bw = bw, Bh = bh, BlockGrid = blockGrid,
            NumBlockClusters = 15, BlockCtxMap = JxlLfGlobalVarDct.DefaultBlockCtxMap,
        };
        List<(int Ctx, uint Value)> hfStream = JxlHfCoeff.Encode(hfParams, enc.HfQuant);

        // Entropy plans: the Modular sample distribution (lf + hf_meta) and the hf_dist distribution.
        (List<(int Ctx, uint Value)> lfStream, List<(int Ctx, uint Value)> hfMetaStream) = lfGroup.BuildStreams();
        JxlEntropyEncoder sampleEnc = JxlModularSubimage.NewSampleEncoder();
        JxlEntropyEncoder.Plan samplePlan = sampleEnc.Prepare(lfStream, hfMetaStream);

        const int ctxSize = 495 * 15;
        var hfDistEnc = new JxlEntropyEncoder(new byte[ctxSize], [JxlIntegerConfig.Create(4, 0, 0)]);
        JxlEntropyEncoder.Plan hfDistPlan = hfDistEnc.Prepare(hfStream);

        byte[] section = BuildSection(globalScale, quantLf, lfGroup, sampleEnc, samplePlan, lfStream, hfMetaStream, hfDistEnc, hfDistPlan, hfStream);

        var bwr = new JxlBitWriter();
        bwr.WriteBits(0xFF, 8);
        bwr.WriteBits(0x0A, 8); // codestream signature FF 0A
        WriteSizeHeader(bwr, width, height);
        bwr.WriteBit(true); // ImageMetadata all_default => xyb_encoded, 8-bit, sRGB, default opsin
        WriteFrameHeader(bwr);
        WriteToc(bwr, section.Length);
        bwr.WriteBytes(section);
        return bwr.ToArray();
    }

    private static byte[] BuildSection(
        int globalScale, int quantLf, JxlLfGroup lfGroup,
        JxlEntropyEncoder sampleEnc, JxlEntropyEncoder.Plan samplePlan,
        List<(int Ctx, uint Value)> lfStream, List<(int Ctx, uint Value)> hfMetaStream,
        JxlEntropyEncoder hfDistEnc, JxlEntropyEncoder.Plan hfDistPlan, List<(int Ctx, uint Value)> hfStream)
    {
        var sec = new JxlBitWriter();

        // LfGlobal: no patches/splines/noise (frame flags = 0); LfChannelDequantization all_default.
        sec.WriteBit(true); // LfChannelDequantization all_default
        new JxlLfGlobalVarDct { GlobalScale = globalScale, QuantLf = quantLf }.Write(sec);

        // GlobalModular: a global tree (used by the LfGroup sub-images) and no colour channels, so the
        // empty stream-0 modular carries no header at all (jxl-modular Modular::parse short-circuits).
        sec.WriteBit(true); // global_tree_present
        JxlModularSubimage.WriteSharedTree(sec, sampleEnc, samplePlan);

        // LfGroup (LfCoeff + HfMetadata).
        lfGroup.Write(sec, sampleEnc, samplePlan, lfStream, hfMetaStream);

        // HfGlobal.
        JxlHfGlobal.Write(sec, numGroups: 1, numHfPresets: 1, hfDistEnc, hfDistPlan);

        // PassGroup: num_hf_presets == 1 => no hfp bits; then the HF coefficient data.
        hfDistEnc.WriteData(sec, hfDistPlan, hfStream);

        return sec.ToArray();
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

    private static void WriteToc(JxlBitWriter bw, int sectionLength)
    {
        bw.WriteBit(false); // permuted = false
        bw.ZeroPadToByte();
        bw.WriteU32((uint)sectionLength, (0, 10), (1024, 14), (17408, 22), (4211712, 30));
        bw.ZeroPadToByte();
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
