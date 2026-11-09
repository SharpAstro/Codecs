namespace SharpAstro.Heif.Hevc;

/// <summary>
/// The slice segment header (H.265 7.3.6.1) of an intra slice of an IDR picture, the only kind decoded, with the byte offset in the RBSP where its slice data (and so its CABAC stream) starts.
/// Everything a still intra picture can carry that this decoder does not implement is refused here by
/// name, before any slice data is read.
/// </summary>
internal sealed record HevcSliceHeader
{
    public required bool FirstInPicture { get; init; }
    public required int SegmentAddress { get; init; }

    /// <summary>SliceAddrRs: the address of the slice's independent segment, which a dependent one inherits.</summary>
    public int SliceAddress { get; init; }

    /// <summary>dependent_slice_segment_flag: this segment continues the slice of the one before it.</summary>
    public bool Dependent { get; init; }

    /// <summary>SliceQpY = 26 + init_qp_minus26 + slice_qp_delta (7.4.7.1).</summary>
    public required int SliceQpY { get; init; }

    public required int CbQpOffset { get; init; }
    public required int CrQpOffset { get; init; }

    /// <summary>slice_sao_luma_flag and slice_sao_chroma_flag: whether this slice's CTBs carry SAO parameters.</summary>
    public bool SaoLuma { get; init; }
    public bool SaoChroma { get; init; }

    /// <summary>slice_deblocking_filter_disabled_flag, and the offsets the filter uses when it is not (7.4.7.1).</summary>
    public bool DeblockingDisabled { get; init; } = true;
    public int BetaOffsetDiv2 { get; init; }
    public int TcOffsetDiv2 { get; init; }

    /// <summary>slice_loop_filter_across_slices_enabled_flag: the filters may cross this slice's left and upper boundary.</summary>
    public bool LoopFilterAcrossSlices { get; init; }

    /// <summary>
    /// Where each substream after the first begins, as RBSP positions, from the entry points (7.4.7.1: offsets
    /// in NAL unit bytes, emulation prevention included). Empty when the slice codes none.
    /// </summary>
    public int[] SubstreamStarts { get; init; } = [];

    /// <summary>The first byte of slice_segment_data() in the RBSP, after byte_alignment().</summary>
    public required int DataOffset { get; init; }

    public const int SliceTypeI = 2;

    /// <summary>
    /// Parses a slice segment header. A dependent slice segment (7.4.7.1) codes only its address and entry
    /// points and takes everything else from <paramref name="independent"/>, the header of the slice it
    /// continues.
    /// </summary>
    public static HevcSliceHeader Parse(HevcNalUnit nal, HevcSps sps, HevcPps pps, HevcSliceHeader? independent = null)
    {
        // Any picture whose slices are all intra decodes on its own: an IDR, a CRA or BLA (which also carry a
        // picture order count and reference picture sets, parsed and unused), or a trailing picture that
        // happens to be intra. Its inter slices are refused below.
        var idr = nal.Type is 19 or 20;

        var r = new RbspReader(nal.Rbsp);
        var first = r.Flag();
        if (nal.IsIrap) r.Flag(); // no_output_of_prior_pics_flag
        var ppsId = r.Ue(63, "slice_pic_parameter_set_id");
        if (ppsId != pps.Id)
            throw new NotSupportedException($"A slice refers to PPS {ppsId}; only the one PPS in 'hvcC' ({pps.Id}) is present.");

        var address = 0;
        var dependent = false;
        if (!first)
        {
            dependent = pps.DependentSliceSegments && r.Flag();
            var ctbs = sps.WidthInCtbs * sps.HeightInCtbs;
            address = (int)r.U(CeilLog2(ctbs));
            if (address >= ctbs) throw new InvalidDataException($"slice_segment_address {address} is past the picture's {ctbs} CTBs.");
        }
        if (dependent)
        {
            if (independent is null) throw new InvalidDataException("A dependent slice segment has no slice to continue.");
            var (dependentStarts, dependentOffset) = ReadTail(ref r, nal, sps, pps);
            return independent with
            {
                FirstInPicture = false,
                SegmentAddress = address,
                Dependent = true,
                SubstreamStarts = dependentStarts,
                DataOffset = dependentOffset,
            };
        }

        r.Skip(pps.NumExtraSliceHeaderBits); // slice_reserved_flag
        var sliceType = r.Ue(2, "slice_type");
        if (sliceType != SliceTypeI)
        {
            if (nal.IsIrap) throw new InvalidDataException($"An IRAP picture has a slice of type {sliceType}; its slices are intra (type 2).");
            throw new NotSupportedException($"An inter-coded (P or B) slice is not decoded: this is a still-picture decoder.");
        }
        if (pps.OutputFlagPresent) r.Flag(); // pic_output_flag
        // colour_plane_id would follow for separate colour planes, which the decoder refuses up front.

        if (!idr)
        {
            // 7.3.6.1: the picture order count and the reference picture sets, which an intra picture never uses.
            r.Skip(sps.Log2MaxPocLsb); // slice_pic_order_cnt_lsb
            if (!r.Flag()) // short_term_ref_pic_set_sps_flag
                HevcShortTermRps.Skip(ref r, sps.NumShortTermRefPicSets, sps.NumShortTermRefPicSets, sps.NumDeltaPocs);
            else if (sps.NumShortTermRefPicSets > 1)
                r.Skip(CeilLog2(sps.NumShortTermRefPicSets)); // short_term_ref_pic_set_idx
            if (sps.LongTermRefPicsPresent)
            {
                var fromSps = sps.NumLongTermRefPicsSps > 0 ? r.Ue(sps.NumLongTermRefPicsSps, "num_long_term_sps") : 0;
                var coded = r.Ue(32, "num_long_term_pics");
                for (var i = 0; i < fromSps + coded; i++)
                {
                    if (i < fromSps)
                    {
                        if (sps.NumLongTermRefPicsSps > 1) r.Skip(CeilLog2(sps.NumLongTermRefPicsSps)); // lt_idx_sps
                    }
                    else
                    {
                        r.Skip(sps.Log2MaxPocLsb); // poc_lsb_lt
                        r.Flag();                  // used_by_curr_pic_lt_flag
                    }
                    if (r.Flag()) r.Ue(); // delta_poc_msb_present_flag, delta_poc_msb_cycle_lt
                }
            }
            if (sps.TemporalMvpEnabled) r.Flag(); // slice_temporal_mvp_enabled_flag
        }

        bool saoLuma = false, saoChroma = false;
        if (sps.SampleAdaptiveOffset)
        {
            saoLuma = r.Flag();
            saoChroma = sps.ChromaFormatIdc != 0 && r.Flag();
        }

        var minQp = -6 * (sps.BitDepthLuma - 8);
        var qp = pps.InitQp + r.Se();
        if (qp < minQp || qp > 51) throw new InvalidDataException($"SliceQpY {qp} is outside [{minQp}, 51].");

        int cb = 0, cr = 0;
        if (pps.SliceChromaQpOffsetsPresent)
        {
            cb = r.Se(-12, 12, "slice_cb_qp_offset");
            cr = r.Se(-12, 12, "slice_cr_qp_offset");
            if (pps.CbQpOffset + cb is < -12 or > 12 || pps.CrQpOffset + cr is < -12 or > 12)
                throw new InvalidDataException("The PPS and slice chroma QP offsets add up past [-12, 12].");
        }
        if (pps.ChromaQpOffsetList && r.Flag())
            throw new NotSupportedException("CU chroma QP offsets are not decoded (rung 4).");

        // Without an override the slice takes the PPS's deblocking settings (7.4.7.1).
        var deblockingDisabled = pps.DeblockingDisabled;
        int beta = pps.BetaOffsetDiv2, tc = pps.TcOffsetDiv2;
        if (pps.DeblockingOverrideEnabled && r.Flag()) // deblocking_filter_override_flag
        {
            deblockingDisabled = r.Flag();
            if (!deblockingDisabled)
            {
                beta = r.Se(-6, 6, "slice_beta_offset_div2");
                tc = r.Se(-6, 6, "slice_tc_offset_div2");
            }
        }
        var across = pps.LoopFilterAcrossSlices;
        if (pps.LoopFilterAcrossSlices && (saoLuma || saoChroma || !deblockingDisabled))
            across = r.Flag(); // slice_loop_filter_across_slices_enabled_flag

        var (starts, dataOffset) = ReadTail(ref r, nal, sps, pps);

        return new HevcSliceHeader
        {
            SubstreamStarts = starts,
            FirstInPicture = first,
            SegmentAddress = address,
            SliceAddress = address,
            SliceQpY = qp,
            CbQpOffset = cb,
            CrQpOffset = cr,
            SaoLuma = saoLuma,
            SaoChroma = saoChroma,
            DeblockingDisabled = deblockingDisabled,
            BetaOffsetDiv2 = beta,
            TcOffsetDiv2 = tc,
            LoopFilterAcrossSlices = across,
            DataOffset = dataOffset,
        };
    }

    /// <summary>
    /// The end of every slice segment header, dependent or not: the entry points (as RBSP positions), the
    /// header extension and byte_alignment(); returns where the slice data begins.
    /// </summary>
    private static (int[] Starts, int DataOffset) ReadTail(ref RbspReader r, HevcNalUnit nal, HevcSps sps, HevcPps pps)
    {
        var entryOffsets = Array.Empty<long>();
        if (pps.Tiles || pps.EntropyCodingSync)
        {
            // Entry points: the size of each substream (a tile, or a CTB row of one) but the last, in NAL unit bytes.
            var maxCount = pps.EntropyCodingSync
                ? (pps.Tiles ? pps.TileColumns * sps.HeightInCtbs : sps.HeightInCtbs) - 1
                : pps.TileColumns * pps.TileRows - 1;
            var count = r.Ue(maxCount, "num_entry_point_offsets");
            if (count > 0)
            {
                var bits = r.Ue(31, "offset_len_minus1") + 1;
                entryOffsets = new long[count];
                for (var i = 0; i < count; i++) entryOffsets[i] = (long)r.U(bits) + 1;
            }
        }

        if (pps.SliceHeaderExtensionPresent)
        {
            var length = r.Ue(256, "slice_segment_header_extension_length");
            r.Skip(length * 8);
        }

        // byte_alignment(): a one bit, then zeros up to the byte boundary.
        if (!r.Flag()) throw new InvalidDataException("The slice header's byte_alignment() does not start with a one bit.");
        while (!r.ByteAligned)
            if (r.Flag()) throw new InvalidDataException("The slice header's byte_alignment() has a non-zero padding bit.");

        var dataOffset = (int)(r.BitPosition >> 3);
        var starts = new int[entryOffsets.Length];
        long payload = nal.PayloadOffset(dataOffset);
        for (var i = 0; i < starts.Length; i++)
        {
            payload += entryOffsets[i];
            var start = payload < int.MaxValue ? nal.RbspPosition((int)payload) : int.MaxValue;
            if (start >= nal.Rbsp.Length || (i > 0 && start <= starts[i - 1]))
                throw new InvalidDataException($"Entry point {i} puts a substream at byte {payload} of a {nal.Rbsp.Length}-byte slice.");
            starts[i] = start;
        }

        return (starts, dataOffset);
    }

    internal static int CeilLog2(int value)
    {
        var bits = 0;
        while ((1 << bits) < value) bits++;
        return bits;
    }
}
