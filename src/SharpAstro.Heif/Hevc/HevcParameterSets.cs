namespace SharpAstro.Heif.Hevc;

/// <summary>
/// The general part of profile_tier_level() (H.265 7.3.3). Sub-layer entries are read past but not kept:
/// a still image has one sub-layer.
/// </summary>
internal sealed record HevcProfileTierLevel(int ProfileSpace, bool HighTier, int ProfileIdc, uint CompatibilityFlags, ulong ConstraintFlags, int LevelIdc)
{
    // The 48 constraint bits as hvcC and the PTL both store them, most significant first: progressive,
    // interlaced, non-packed, frame-only, then (for the range-extension profiles, 7.4.4) max_12bit,
    // max_10bit, max_8bit, max_422chroma, max_420chroma, max_monochrome, intra, one_picture_only,
    // lower_bit_rate.
    private bool Bit(int fromTop) => (ConstraintFlags >> (47 - fromTop) & 1) != 0;

    public bool IntraConstraint => Bit(10);
    public bool OnePictureOnly => Bit(11);

    public static HevcProfileTierLevel Read(ref RbspReader r, int maxSubLayersMinus1)
    {
        var space = (int)r.U(2);
        var tier = r.Flag();
        var idc = (int)r.U(5);
        var compatibility = r.U(32);
        var constraints = (ulong)r.U(32) << 16 | r.U(16);
        var level = (int)r.U(8);

        Span<bool> profilePresent = stackalloc bool[8];
        Span<bool> levelPresent = stackalloc bool[8];
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = r.Flag();
            levelPresent[i] = r.Flag();
        }
        if (maxSubLayersMinus1 > 0)
            for (var i = maxSubLayersMinus1; i < 8; i++) r.Skip(2); // reserved_zero_2bits
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i]) r.Skip(88);
            if (levelPresent[i]) r.Skip(8);
        }

        return new HevcProfileTierLevel(space, tier, idc, compatibility, constraints, level);
    }
}

/// <summary>The flags of sps_range_extension() (H.265 7.3.2.2.2), all false when the extension is absent.</summary>
internal sealed record HevcSpsRangeExtension(
    bool TransformSkipRotation, bool TransformSkipContext, bool ImplicitRdpcm, bool ExplicitRdpcm,
    bool ExtendedPrecisionProcessing, bool IntraSmoothingDisabled, bool HighPrecisionOffsets,
    bool PersistentRiceAdaptation, bool CabacBypassAlignment)
{
    public static readonly HevcSpsRangeExtension None = new(false, false, false, false, false, false, false, false, false);

    public bool Any => this != None;
}

/// <summary>The colour and range a VUI states (E.2.1), when it states them.</summary>
internal sealed record HevcVideoSignal(int VideoFormat, bool FullRange, int? Primaries, int? Transfer, int? Matrix, int? ChromaLocationTop);

/// <summary>A sequence parameter set (H.265 7.3.2.2), the fields a still-image decoder needs.</summary>
internal sealed record HevcSps
{
    public required int Id { get; init; }
    public required HevcProfileTierLevel ProfileTierLevel { get; init; }
    public required int ChromaFormatIdc { get; init; }
    public required bool SeparateColourPlanes { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Conformance window offsets in luma samples (already scaled by SubWidthC / SubHeightC).</summary>
    public required (int Left, int Right, int Top, int Bottom) ConformanceWindow { get; init; }
    public required int BitDepthLuma { get; init; }
    public required int BitDepthChroma { get; init; }
    public required int Log2MaxPocLsb { get; init; }
    public required int Log2MinCodingBlockSize { get; init; }
    public required int Log2CtbSize { get; init; }
    public required int Log2MinTransformBlockSize { get; init; }
    public required int Log2MaxTransformBlockSize { get; init; }
    public required int MaxTransformHierarchyDepthInter { get; init; }
    public required int MaxTransformHierarchyDepthIntra { get; init; }
    public required bool ScalingListEnabled { get; init; }
    public required bool ScalingListDataInSps { get; init; }

    /// <summary>The lists coded in the SPS, or null when none are (the defaults then apply if scaling lists are enabled).</summary>
    public required HevcScalingList? ScalingList { get; init; }
    public required bool AmpEnabled { get; init; }
    public required bool SampleAdaptiveOffset { get; init; }
    public required bool PcmEnabled { get; init; }
    public required int NumShortTermRefPicSets { get; init; }
    public required bool LongTermRefPicsPresent { get; init; }
    public required bool TemporalMvpEnabled { get; init; }
    public required bool StrongIntraSmoothing { get; init; }
    public required HevcVideoSignal? VideoSignal { get; init; }
    public required HevcSpsRangeExtension RangeExtension { get; init; }
    /// <summary>The multilayer, 3D and SCC extension flags, which this reader does not interpret further.</summary>
    public required bool OtherExtensions { get; init; }

    public int CtbSize => 1 << Log2CtbSize;
    public int WidthInCtbs => (Width + CtbSize - 1) >> Log2CtbSize;
    public int HeightInCtbs => (Height + CtbSize - 1) >> Log2CtbSize;

    public static HevcSps Parse(ReadOnlySpan<byte> rbsp)
    {
        var r = new RbspReader(rbsp);
        r.U(4); // sps_video_parameter_set_id
        var maxSubLayersMinus1 = (int)r.U(3);
        if (maxSubLayersMinus1 > 6) throw new InvalidDataException($"sps_max_sub_layers_minus1 = {maxSubLayersMinus1} exceeds 6.");
        r.Flag(); // sps_temporal_id_nesting_flag
        var ptl = HevcProfileTierLevel.Read(ref r, maxSubLayersMinus1);

        var id = r.Ue(15, "sps_seq_parameter_set_id");
        var chroma = r.Ue(3, "chroma_format_idc");
        var separate = chroma == 3 && r.Flag();
        var width = r.Ue(HeifLimits.MaxCodedDimension, "pic_width_in_luma_samples");
        var height = r.Ue(HeifLimits.MaxCodedDimension, "pic_height_in_luma_samples");
        if (width == 0 || height == 0) throw new InvalidDataException($"The SPS declares an empty picture {width}x{height}.");

        var (subW, subH) = chroma switch { 1 => (2, 2), 2 => (2, 1), _ => (1, 1) };
        var window = (0, 0, 0, 0);
        if (r.Flag())
        {
            var left = r.Ue(width, "conf_win_left_offset") * subW;
            var right = r.Ue(width, "conf_win_right_offset") * subW;
            var top = r.Ue(height, "conf_win_top_offset") * subH;
            var bottom = r.Ue(height, "conf_win_bottom_offset") * subH;
            if (left + right >= width || top + bottom >= height)
                throw new InvalidDataException("The SPS conformance window leaves no picture.");
            window = (left, right, top, bottom);
        }

        var depthLuma = r.Ue(8, "bit_depth_luma_minus8") + 8;
        var depthChroma = r.Ue(8, "bit_depth_chroma_minus8") + 8;
        var log2MaxPocLsb = r.Ue(12, "log2_max_pic_order_cnt_lsb_minus4") + 4;

        var orderingAll = r.Flag();
        for (var i = orderingAll ? 0 : maxSubLayersMinus1; i <= maxSubLayersMinus1; i++)
        {
            r.Ue(15, "sps_max_dec_pic_buffering_minus1");
            r.Ue(15, "sps_max_num_reorder_pics");
            r.Ue(); // sps_max_latency_increase_plus1
        }

        var log2MinCb = r.Ue(3, "log2_min_luma_coding_block_size_minus3") + 3;
        var log2Ctb = log2MinCb + r.Ue(3, "log2_diff_max_min_luma_coding_block_size");
        if (log2Ctb is < 4 or > 6) throw new InvalidDataException($"The CTB size 2^{log2Ctb} is outside 16..64.");
        var log2MinTb = r.Ue(3, "log2_min_luma_transform_block_size_minus2") + 2;
        var log2MaxTb = log2MinTb + r.Ue(3, "log2_diff_max_min_luma_transform_block_size");
        if (log2MinTb >= log2MinCb || log2MaxTb > Math.Min(log2Ctb, 5))
            throw new InvalidDataException($"The transform block sizes 2^{log2MinTb}..2^{log2MaxTb} do not fit the coding blocks.");
        var depthInter = r.Ue(log2Ctb - log2MinTb, "max_transform_hierarchy_depth_inter");
        var depthIntra = r.Ue(log2Ctb - log2MinTb, "max_transform_hierarchy_depth_intra");

        var scalingEnabled = r.Flag();
        var scalingInSps = false;
        HevcScalingList? scalingList = null;
        if (scalingEnabled)
        {
            scalingInSps = r.Flag();
            if (scalingInSps) scalingList = HevcScalingList.Read(ref r);
        }

        var amp = r.Flag();
        var sao = r.Flag();
        var pcm = r.Flag();
        if (pcm)
        {
            r.U(4); r.U(4); // pcm sample bit depths
            r.Ue(); r.Ue(); // pcm block sizes
            r.Flag();       // pcm_loop_filter_disabled_flag
        }

        var numStRps = r.Ue(64, "num_short_term_ref_pic_sets");
        var numDeltaPocs = new int[numStRps];
        for (var i = 0; i < numStRps; i++)
            numDeltaPocs[i] = HevcShortTermRps.Skip(ref r, i, numStRps, numDeltaPocs);

        var longTerm = r.Flag();
        if (longTerm)
        {
            var count = r.Ue(32, "num_long_term_ref_pics_sps");
            for (var i = 0; i < count; i++) { r.U(log2MaxPocLsb); r.Flag(); }
        }

        var temporalMvp = r.Flag();
        var strongSmoothing = r.Flag();

        HevcVideoSignal? signal = null;
        if (r.Flag()) signal = HevcVui.Read(ref r, maxSubLayersMinus1);

        var range = HevcSpsRangeExtension.None;
        var other = false;
        if (r.Flag()) // sps_extension_present_flag
        {
            var rangeFlag = r.Flag();
            var multilayer = r.Flag();
            var threeD = r.Flag();
            var scc = r.Flag();
            var extension4 = r.U(4);
            other = multilayer || threeD || scc || extension4 != 0;
            if (rangeFlag)
                range = new HevcSpsRangeExtension(r.Flag(), r.Flag(), r.Flag(), r.Flag(), r.Flag(), r.Flag(), r.Flag(), r.Flag(), r.Flag());
        }

        return new HevcSps
        {
            Id = id,
            ProfileTierLevel = ptl,
            ChromaFormatIdc = chroma,
            SeparateColourPlanes = separate,
            Width = width,
            Height = height,
            ConformanceWindow = window,
            BitDepthLuma = depthLuma,
            BitDepthChroma = depthChroma,
            Log2MaxPocLsb = log2MaxPocLsb,
            Log2MinCodingBlockSize = log2MinCb,
            Log2CtbSize = log2Ctb,
            Log2MinTransformBlockSize = log2MinTb,
            Log2MaxTransformBlockSize = log2MaxTb,
            MaxTransformHierarchyDepthInter = depthInter,
            MaxTransformHierarchyDepthIntra = depthIntra,
            ScalingListEnabled = scalingEnabled,
            ScalingListDataInSps = scalingInSps,
            ScalingList = scalingList,
            AmpEnabled = amp,
            SampleAdaptiveOffset = sao,
            PcmEnabled = pcm,
            NumShortTermRefPicSets = numStRps,
            LongTermRefPicsPresent = longTerm,
            TemporalMvpEnabled = temporalMvp,
            StrongIntraSmoothing = strongSmoothing,
            VideoSignal = signal,
            RangeExtension = range,
            OtherExtensions = other,
        };
    }
}

/// <summary>A picture parameter set (H.265 7.3.2.3), the fields a still-image decoder needs.</summary>
internal sealed record HevcPps
{
    public required int Id { get; init; }
    public required int SpsId { get; init; }
    public required bool DependentSliceSegments { get; init; }
    public required bool OutputFlagPresent { get; init; }
    public required int NumExtraSliceHeaderBits { get; init; }
    public required bool SignDataHiding { get; init; }
    public required bool CabacInitPresent { get; init; }
    public required int InitQp { get; init; }
    public required bool ConstrainedIntraPred { get; init; }
    public required bool TransformSkip { get; init; }
    public required bool CuQpDeltaEnabled { get; init; }
    public required int DiffCuQpDeltaDepth { get; init; }
    public required int CbQpOffset { get; init; }
    public required int CrQpOffset { get; init; }
    public required bool SliceChromaQpOffsetsPresent { get; init; }
    public required bool TransquantBypass { get; init; }
    public required bool Tiles { get; init; }
    public required int TileColumns { get; init; }
    public required int TileRows { get; init; }
    public required bool EntropyCodingSync { get; init; }
    public required bool LoopFilterAcrossTiles { get; init; }
    public required bool LoopFilterAcrossSlices { get; init; }
    public required bool DeblockingOverrideEnabled { get; init; }
    public required bool DeblockingDisabled { get; init; }
    public required int BetaOffsetDiv2 { get; init; }
    public required int TcOffsetDiv2 { get; init; }
    public required bool ScalingListDataInPps { get; init; }

    /// <summary>The lists coded in the PPS, which replace the SPS's for pictures using it; null when none are.</summary>
    public required HevcScalingList? ScalingList { get; init; }
    public required bool SliceHeaderExtensionPresent { get; init; }
    public required int Log2MaxTransformSkipBlockSize { get; init; }
    public required bool CrossComponentPrediction { get; init; }
    public required bool ChromaQpOffsetList { get; init; }
    public required int Log2SaoOffsetScaleLuma { get; init; }
    public required int Log2SaoOffsetScaleChroma { get; init; }
    public required bool OtherExtensions { get; init; }

    public static HevcPps Parse(ReadOnlySpan<byte> rbsp)
    {
        var r = new RbspReader(rbsp);
        var id = r.Ue(63, "pps_pic_parameter_set_id");
        var spsId = r.Ue(15, "pps_seq_parameter_set_id");
        var dependent = r.Flag();
        var outputFlag = r.Flag();
        var extraBits = (int)r.U(3);
        var signHiding = r.Flag();
        var cabacInit = r.Flag();
        r.Ue(14, "num_ref_idx_l0_default_active_minus1");
        r.Ue(14, "num_ref_idx_l1_default_active_minus1");
        // init_qp_minus26 ranges down to -(26 + QpBdOffsetY), which needs the SPS; the widest bound (16-bit) is used.
        var initQp = 26 + r.Se(-(26 + 48), 25, "init_qp_minus26");
        var constrainedIntra = r.Flag();
        var transformSkip = r.Flag();
        var cuQpDelta = r.Flag();
        var cuQpDepth = cuQpDelta ? r.Ue(3, "diff_cu_qp_delta_depth") : 0;
        var cbOffset = r.Se(-12, 12, "pps_cb_qp_offset");
        var crOffset = r.Se(-12, 12, "pps_cr_qp_offset");
        var sliceChroma = r.Flag();
        r.Flag(); // weighted_pred_flag
        r.Flag(); // weighted_bipred_flag
        var bypass = r.Flag();
        var tiles = r.Flag();
        var sync = r.Flag();

        int columns = 1, rows = 1;
        var acrossTiles = true;
        if (tiles)
        {
            columns = r.Ue(HeifLimits.MaxCodedDimension / 256, "num_tile_columns_minus1") + 1;
            rows = r.Ue(HeifLimits.MaxCodedDimension / 64, "num_tile_rows_minus1") + 1;
            if (!r.Flag()) // uniform_spacing_flag
            {
                for (var i = 0; i < columns - 1; i++) r.Ue();
                for (var i = 0; i < rows - 1; i++) r.Ue();
            }
            acrossTiles = r.Flag();
        }

        var acrossSlices = r.Flag();
        bool overrideEnabled = false, disabled = false;
        int beta = 0, tc = 0;
        if (r.Flag()) // deblocking_filter_control_present_flag
        {
            overrideEnabled = r.Flag();
            disabled = r.Flag();
            if (!disabled)
            {
                beta = r.Se(-6, 6, "pps_beta_offset_div2");
                tc = r.Se(-6, 6, "pps_tc_offset_div2");
            }
        }

        var scalingInPps = r.Flag();
        var ppsScalingList = scalingInPps ? HevcScalingList.Read(ref r) : null;
        r.Flag(); // lists_modification_present_flag
        r.Ue();   // log2_parallel_merge_level_minus2
        var headerExtension = r.Flag();

        int log2MaxTs = 2, saoLuma = 0, saoChroma = 0;
        bool crossComponent = false, chromaQpList = false, other = false;
        if (r.Flag()) // pps_extension_present_flag
        {
            var rangeFlag = r.Flag();
            var multilayer = r.Flag();
            var threeD = r.Flag();
            var scc = r.Flag();
            var extension4 = r.U(4);
            other = multilayer || threeD || scc || extension4 != 0;
            if (rangeFlag)
            {
                if (transformSkip) log2MaxTs = r.Ue(3, "log2_max_transform_skip_block_size_minus2") + 2;
                crossComponent = r.Flag();
                chromaQpList = r.Flag();
                if (chromaQpList)
                {
                    r.Ue(); // diff_cu_chroma_qp_offset_depth
                    var length = r.Ue(5, "chroma_qp_offset_list_len_minus1") + 1;
                    for (var i = 0; i < length; i++) { r.Se(); r.Se(); }
                }
                saoLuma = r.Ue(6, "log2_sao_offset_scale_luma");
                saoChroma = r.Ue(6, "log2_sao_offset_scale_chroma");
            }
        }

        return new HevcPps
        {
            Id = id,
            SpsId = spsId,
            DependentSliceSegments = dependent,
            OutputFlagPresent = outputFlag,
            NumExtraSliceHeaderBits = extraBits,
            SignDataHiding = signHiding,
            CabacInitPresent = cabacInit,
            InitQp = initQp,
            ConstrainedIntraPred = constrainedIntra,
            TransformSkip = transformSkip,
            CuQpDeltaEnabled = cuQpDelta,
            DiffCuQpDeltaDepth = cuQpDepth,
            CbQpOffset = cbOffset,
            CrQpOffset = crOffset,
            SliceChromaQpOffsetsPresent = sliceChroma,
            TransquantBypass = bypass,
            Tiles = tiles,
            TileColumns = columns,
            TileRows = rows,
            EntropyCodingSync = sync,
            LoopFilterAcrossTiles = acrossTiles,
            LoopFilterAcrossSlices = acrossSlices,
            DeblockingOverrideEnabled = overrideEnabled,
            DeblockingDisabled = disabled,
            BetaOffsetDiv2 = beta,
            TcOffsetDiv2 = tc,
            ScalingListDataInPps = scalingInPps,
            ScalingList = ppsScalingList,
            SliceHeaderExtensionPresent = headerExtension,
            Log2MaxTransformSkipBlockSize = log2MaxTs,
            CrossComponentPrediction = crossComponent,
            ChromaQpOffsetList = chromaQpList,
            Log2SaoOffsetScaleLuma = saoLuma,
            Log2SaoOffsetScaleChroma = saoChroma,
            OtherExtensions = other,
        };
    }
}

/// <summary>
/// scaling_list_data() (H.265 7.3.4, semantics 7.4.5): the quantisation weighting matrices, kept as coded
/// (each list in up-right diagonal order, plus the DC value of the 16x16 and 32x32 lists) and expanded on
/// demand into <c>ScalingFactor</c>, the per-coefficient weight the scaling process multiplies by.
/// </summary>
internal sealed class HevcScalingList
{
    /// <summary>Table 7-6, intra (matrixId 0..2) lists for sizeId 1..3, in diagonal order.</summary>
    private static readonly byte[] DefaultIntra =
    [
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 17, 16, 17, 16, 17, 18, 17, 18, 18, 17, 18, 21, 19, 20, 21, 20, 19, 21, 24, 22, 22, 24,
        24, 22, 22, 24, 25, 25, 27, 30, 27, 25, 25, 29, 31, 35, 35, 31, 29, 36, 41, 44, 41, 36, 47, 54, 54, 47, 65, 70, 65, 88, 88, 115,
    ];

    /// <summary>Table 7-6, inter (matrixId 3..5) lists for sizeId 1..3, in diagonal order.</summary>
    private static readonly byte[] DefaultInter =
    [
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 17, 17, 17, 17, 17, 18, 18, 18, 18, 18, 18, 20, 20, 20, 20, 20, 20, 20, 24, 24, 24, 24,
        24, 24, 24, 24, 25, 25, 25, 25, 25, 25, 25, 28, 28, 28, 28, 28, 28, 33, 33, 33, 33, 33, 41, 41, 41, 41, 54, 54, 54, 71, 71, 91,
    ];

    // [sizeId][matrixId]: the coded list (16 entries for sizeId 0, else 64) and the DC value (sizeId 2 and 3).
    private readonly byte[][][] _lists = new byte[4][][];
    private readonly int[][] _dc = new int[4][];
    private readonly int[]?[][] _factors = new int[]?[4][];

    private HevcScalingList()
    {
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            _lists[sizeId] = new byte[6][];
            _dc[sizeId] = new int[6];
            _factors[sizeId] = new int[]?[6];
        }
    }

    /// <summary>The default lists (Table 7-5 / 7-6), used when scaling lists are enabled but none is coded.</summary>
    public static HevcScalingList Default { get; } = CreateDefault();

    private static HevcScalingList CreateDefault()
    {
        var lists = new HevcScalingList();
        for (var sizeId = 0; sizeId < 4; sizeId++)
            for (var matrixId = 0; matrixId < 6; matrixId++)
                lists.SetDefault(sizeId, matrixId);
        return lists;
    }

    private void SetDefault(int sizeId, int matrixId)
    {
        _lists[sizeId][matrixId] = sizeId == 0 ? Enumerable.Repeat((byte)16, 16).ToArray() : (matrixId < 3 ? DefaultIntra : DefaultInter);
        _dc[sizeId][matrixId] = 16;
    }

    public static HevcScalingList Read(ref RbspReader r)
    {
        var lists = new HevcScalingList();
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            var step = sizeId == 3 ? 3 : 1;
            for (var matrixId = 0; matrixId < 6; matrixId += step)
            {
                if (!r.Flag()) // scaling_list_pred_mode_flag
                {
                    var delta = r.Ue(matrixId / step, "scaling_list_pred_matrix_id_delta");
                    if (delta == 0)
                    {
                        lists.SetDefault(sizeId, matrixId);
                    }
                    else
                    {
                        var reference = matrixId - delta * step;
                        lists._lists[sizeId][matrixId] = lists._lists[sizeId][reference];
                        lists._dc[sizeId][matrixId] = lists._dc[sizeId][reference];
                    }
                    continue;
                }

                var coefficients = Math.Min(64, 1 << (4 + (sizeId << 1)));
                var next = 8;
                if (sizeId > 1)
                {
                    next = r.Se(-7, 247, "scaling_list_dc_coef_minus8") + 8;
                    lists._dc[sizeId][matrixId] = next;
                }
                var list = new byte[coefficients];
                for (var i = 0; i < coefficients; i++)
                {
                    next = (next + r.Se(-128, 127, "scaling_list_delta_coef") + 256) % 256;
                    // 7.4.5: every ScalingList entry is greater than 0; a zero weight would erase the coefficient.
                    if (next == 0) throw new InvalidDataException($"Scaling list {sizeId}/{matrixId} has a zero entry at {i}.");
                    list[i] = (byte)next;
                }
                lists._lists[sizeId][matrixId] = list;
            }
        }

        // The 32x32 lists exist only for matrixId 0 and 3 here; the chroma ones (4:4:4 only) are not coded
        // in this version of the syntax. They are left at their defaults rather than unset.
        foreach (var matrixId in (int[])[1, 2, 4, 5])
            lists.SetDefault(3, matrixId);
        return lists;
    }

    /// <summary>
    /// <c>ScalingFactor[sizeId][matrixId]</c> (7.4.5) for a block of 2^<paramref name="log2Size"/> a side,
    /// indexed <c>[y * size + x]</c>. 4x4 and 8x8 place the list along the diagonal scan; 16x16 and 32x32
    /// replicate the 8x8 list over 2x2 or 4x4 squares and then put the DC value at (0, 0).
    /// </summary>
    public int[] Factors(int log2Size, int matrixId)
    {
        var sizeId = log2Size - 2;
        if (_factors[sizeId][matrixId] is { } cached) return cached;

        var size = 1 << log2Size;
        var factors = new int[size * size];
        var list = _lists[sizeId][matrixId];
        if (sizeId == 0)
        {
            for (var i = 0; i < 16; i++)
                factors[HevcScan.Y(2, 0, i) * 4 + HevcScan.X(2, 0, i)] = list[i];
        }
        else
        {
            var ratio = size / 8;
            for (var i = 0; i < 64; i++)
            {
                var x0 = HevcScan.X(3, 0, i) * ratio;
                var y0 = HevcScan.Y(3, 0, i) * ratio;
                for (var j = 0; j < ratio; j++)
                    for (var k = 0; k < ratio; k++)
                        factors[(y0 + j) * size + x0 + k] = list[i];
            }
            if (sizeId > 1) factors[0] = _dc[sizeId][matrixId];
        }
        return _factors[sizeId][matrixId] = factors;
    }
}

/// <summary>st_ref_pic_set() (H.265 7.3.7), read past. A still image never refers to another picture.</summary>
internal static class HevcShortTermRps
{
    /// <summary>Reads set <paramref name="index"/> of an SPS and returns its NumDeltaPocs.</summary>
    public static int Skip(ref RbspReader r, int index, int numSets, int[] numDeltaPocs)
    {
        if (index != 0 && r.Flag()) // inter_ref_pic_set_prediction_flag
        {
            // delta_idx_minus1 is only present when parsing in a slice header (index == numSets); never here.
            var refIndex = index - 1;
            r.Flag(); // delta_rps_sign
            r.Ue((1 << 15) - 1, "abs_delta_rps_minus1");
            var count = 0;
            for (var j = 0; j <= numDeltaPocs[refIndex]; j++)
            {
                var used = r.Flag();
                var useDelta = used || r.Flag();
                if (useDelta) count++;
            }
            return count;
        }

        var negative = r.Ue(16, "num_negative_pics");
        var positive = r.Ue(16 - negative, "num_positive_pics");
        for (var i = 0; i < negative + positive; i++)
        {
            r.Ue((1 << 15) - 1, "delta_poc_minus1");
            r.Flag();
        }
        return negative + positive;
    }
}

/// <summary>vui_parameters() (H.265 E.2.1), keeping the video signal description.</summary>
internal static class HevcVui
{
    public static HevcVideoSignal? Read(ref RbspReader r, int maxSubLayersMinus1)
    {
        if (r.Flag()) // aspect_ratio_info_present_flag
        {
            if (r.U(8) == 255) { r.U(16); r.U(16); }
        }
        if (r.Flag()) r.Flag(); // overscan

        HevcVideoSignal? signal = null;
        if (r.Flag()) // video_signal_type_present_flag
        {
            var format = (int)r.U(3);
            var fullRange = r.Flag();
            int? primaries = null, transfer = null, matrix = null;
            if (r.Flag())
            {
                primaries = (int)r.U(8);
                transfer = (int)r.U(8);
                matrix = (int)r.U(8);
            }
            signal = new HevcVideoSignal(format, fullRange, primaries, transfer, matrix, null);
        }

        if (r.Flag()) // chroma_loc_info_present_flag
        {
            var top = r.Ue(5, "chroma_sample_loc_type_top_field");
            r.Ue(5, "chroma_sample_loc_type_bottom_field");
            signal = (signal ?? new HevcVideoSignal(5, false, null, null, null, null)) with { ChromaLocationTop = top };
        }

        r.Flag(); // neutral_chroma_indication_flag
        r.Flag(); // field_seq_flag
        r.Flag(); // frame_field_info_present_flag
        if (r.Flag()) { r.Ue(); r.Ue(); r.Ue(); r.Ue(); } // default display window

        if (r.Flag()) // vui_timing_info_present_flag
        {
            r.U(32); r.U(32);
            if (r.Flag()) r.Ue(); // poc proportional to timing
            if (r.Flag()) HevcHrd.Skip(ref r, true, maxSubLayersMinus1);
        }

        if (r.Flag()) // bitstream_restriction_flag
        {
            r.Flag(); r.Flag(); r.Flag();
            r.Ue(); r.Ue(); r.Ue(); r.Ue(); r.Ue();
        }

        return signal;
    }
}

/// <summary>hrd_parameters() (H.265 E.2.2), read past.</summary>
internal static class HevcHrd
{
    public static void Skip(ref RbspReader r, bool commonInfoPresent, int maxSubLayersMinus1)
    {
        bool nal = false, vcl = false, subPic = false;
        if (commonInfoPresent)
        {
            nal = r.Flag();
            vcl = r.Flag();
            if (nal || vcl)
            {
                subPic = r.Flag();
                if (subPic) { r.U(8); r.U(5); r.Flag(); r.U(5); }
                r.U(4); r.U(4);
                if (subPic) r.U(4);
                r.U(5); r.U(5); r.U(5);
            }
        }

        for (var i = 0; i <= maxSubLayersMinus1; i++)
        {
            var fixedGeneral = r.Flag();
            var fixedWithinCvs = fixedGeneral || r.Flag();
            var lowDelay = false;
            if (fixedWithinCvs) r.Ue();
            else lowDelay = r.Flag();
            var cpbCount = lowDelay ? 1 : r.Ue(31, "cpb_cnt_minus1") + 1;
            for (var pass = 0; pass < (nal ? 1 : 0) + (vcl ? 1 : 0); pass++)
            {
                for (var j = 0; j < cpbCount; j++)
                {
                    r.Ue(); r.Ue();
                    if (subPic) { r.Ue(); r.Ue(); }
                    r.Flag();
                }
            }
        }
    }
}
