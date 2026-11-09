using System.Text;

namespace SharpAstro.Heif.Hevc;

/// <summary>An SEI message's type and size (H.265 7.3.5), and for a decoded picture hash (132) its hash type.</summary>
internal sealed record HevcSeiMessage(bool Suffix, int PayloadType, int PayloadSize, int? HashType);

/// <summary>The first fields of a slice segment header (H.265 7.3.6.1), enough to see how a picture is cut up.</summary>
internal sealed record HevcSliceStart(int NalType, bool FirstInPicture, bool Dependent, int SegmentAddress, int? SliceType, int Bytes);

/// <summary>
/// What one coded HEVC image in a HEIF file uses: its parameter sets, the NAL unit types and SEI messages
/// in its data, and how its slices are laid out. This is the measured feature envelope a decoder has to
/// meet, and it checks that the <c>hvcC</c> summary agrees with the SPS it carries.
/// </summary>
internal sealed record HevcSurvey(
    HevcDecoderConfiguration Configuration,
    HevcSps Sps,
    HevcPps Pps,
    IReadOnlyDictionary<int, int> NalTypeCounts,
    IReadOnlyList<HevcSeiMessage> Sei,
    IReadOnlyList<HevcSliceStart> Slices)
{
    public static HevcSurvey Of(HevcDecoderConfiguration configuration, ReadOnlySpan<byte> itemData)
    {
        var spsNals = configuration.NalUnitsOfType(HevcNalUnit.SpsNut).ToList();
        var ppsNals = configuration.NalUnitsOfType(HevcNalUnit.PpsNut).ToList();
        if (spsNals.Count != 1 || ppsNals.Count != 1)
            throw new NotSupportedException($"'hvcC' carries {spsNals.Count} SPS and {ppsNals.Count} PPS; one of each is handled.");

        var sps = HevcSps.Parse(HevcNalUnit.Parse(spsNals[0]).Rbsp);
        var pps = HevcPps.Parse(HevcNalUnit.Parse(ppsNals[0]).Rbsp);
        if (pps.SpsId != sps.Id)
            throw new InvalidDataException($"The PPS refers to SPS {pps.SpsId}, but the SPS is {sps.Id}.");
        CheckAgreement(configuration, sps);

        var counts = new SortedDictionary<int, int>();
        var sei = new List<HevcSeiMessage>();
        var slices = new List<HevcSliceStart>();

        foreach (var arrayNal in configuration.NalUnitArrays.SelectMany(a => a.NalUnits))
        {
            var nal = HevcNalUnit.Parse(arrayNal);
            if (nal.Type is HevcNalUnit.PrefixSeiNut or HevcNalUnit.SuffixSeiNut) sei.AddRange(ReadSei(nal));
        }

        foreach (var bytes in HevcNalUnit.SplitLengthPrefixed(itemData, configuration.LengthSize))
        {
            var nal = HevcNalUnit.Parse(bytes);
            counts[nal.Type] = counts.GetValueOrDefault(nal.Type) + 1;
            if (nal.Type is HevcNalUnit.PrefixSeiNut or HevcNalUnit.SuffixSeiNut) sei.AddRange(ReadSei(nal));
            else if (nal.IsSlice) slices.Add(ReadSliceStart(nal, sps, pps, bytes.Length));
        }

        return new HevcSurvey(configuration, sps, pps, counts, sei, slices);
    }

    /// <summary>
    /// The hvcC summary must say what the SPS says about what a reader sizes and converts from it, the chroma
    /// format and bit depths: a mismatch there means one of them is wrong, and the HEIF side would describe
    /// planes the decoder does not produce. Profile, tier and level are not checked: decoding depends on the
    /// SPS alone, and a file whose hvcC states a different (often a higher, rounded-up) level still decodes.
    /// </summary>
    internal static void CheckAgreement(HevcDecoderConfiguration c, HevcSps sps)
    {
        var chromaDepth = sps.ChromaFormatIdc == 0 ? c.BitDepthChroma : sps.BitDepthChroma; // monochrome: no chroma to agree on
        if ((int)c.ChromaFormat != sps.ChromaFormatIdc || c.BitDepthLuma != sps.BitDepthLuma || c.BitDepthChroma != chromaDepth)
            throw new InvalidDataException(
                $"'hvcC' says chroma format {(int)c.ChromaFormat} at {c.BitDepthLuma}/{c.BitDepthChroma} bits, " +
                $"the SPS chroma format {sps.ChromaFormatIdc} at {sps.BitDepthLuma}/{sps.BitDepthChroma} bits.");
    }

    private static List<HevcSeiMessage> ReadSei(HevcNalUnit nal)
    {
        var messages = new List<HevcSeiMessage>();
        var data = nal.Rbsp;
        var position = 0;
        // sei_rbsp: messages until the trailing bits. Each type and size is a run of 0xFF bytes plus a last byte.
        while (position < data.Length && !(data.Length - position == 1 && data[position] == 0x80))
        {
            var type = ReadSeiNumber(data, ref position);
            var size = ReadSeiNumber(data, ref position);
            if (size > data.Length - position)
                throw new InvalidDataException($"An SEI message of type {type} declares {size} bytes, past the end of its NAL unit.");
            int? hashType = type == 132 && size > 0 ? data[position] : null;
            messages.Add(new HevcSeiMessage(nal.Type == HevcNalUnit.SuffixSeiNut, type, size, hashType));
            position += size;
        }
        return messages;
    }

    private static int ReadSeiNumber(byte[] data, ref int position)
    {
        var value = 0;
        while (true)
        {
            if (position >= data.Length) throw new InvalidDataException("An SEI message header is cut short.");
            var b = data[position++];
            value += b;
            if (b != 0xFF) return value;
            if (value > 1 << 20) throw new InvalidDataException("An SEI payload type or size is implausibly large.");
        }
    }

    private static HevcSliceStart ReadSliceStart(HevcNalUnit nal, HevcSps sps, HevcPps pps, int bytes)
    {
        var r = new RbspReader(nal.Rbsp);
        var first = r.Flag();
        if (nal.IsIrap) r.Flag(); // no_output_of_prior_pics_flag
        var ppsId = r.Ue(63, "slice_pic_parameter_set_id");
        if (ppsId != pps.Id)
            throw new NotSupportedException($"A slice refers to PPS {ppsId}; only the one PPS in 'hvcC' ({pps.Id}) is present.");

        var dependent = false;
        var address = 0;
        if (!first)
        {
            if (pps.DependentSliceSegments) dependent = r.Flag();
            var ctbs = sps.WidthInCtbs * sps.HeightInCtbs;
            address = (int)r.U(CeilLog2(ctbs));
            if (address >= ctbs) throw new InvalidDataException($"slice_segment_address {address} is past the picture's {ctbs} CTBs.");
        }

        int? sliceType = null;
        if (!dependent)
        {
            r.Skip(pps.NumExtraSliceHeaderBits);
            sliceType = r.Ue(2, "slice_type");
        }

        return new HevcSliceStart(nal.Type, first, dependent, address, sliceType, bytes);
    }

    private static int CeilLog2(int value)
    {
        var bits = 0;
        while ((1 << bits) < value) bits++;
        return bits;
    }

    /// <summary>A human-readable account, for the roadmap and for test output.</summary>
    public string Report()
    {
        var c = Configuration;
        var s = Sps;
        var p = Pps;
        var ptl = s.ProfileTierLevel;
        var text = new StringBuilder();
        text.AppendLine($"profile {ptl.ProfileIdc} ({ProfileName(ptl.ProfileIdc)}), {(ptl.HighTier ? "High" : "Main")} tier, level {ptl.LevelIdc / 30.0:0.#}, " +
                        $"intra-only constraint {ptl.IntraConstraint}, one-picture-only {ptl.OnePictureOnly}");
        text.AppendLine($"picture {s.Width}x{s.Height}, conformance window L{s.ConformanceWindow.Left} R{s.ConformanceWindow.Right} T{s.ConformanceWindow.Top} B{s.ConformanceWindow.Bottom}, " +
                        $"chroma_format_idc {s.ChromaFormatIdc}{(s.SeparateColourPlanes ? " (separate planes)" : "")}, {s.BitDepthLuma}/{s.BitDepthChroma}-bit, hvcC length size {c.LengthSize}");
        text.AppendLine($"CTB {s.CtbSize} ({s.WidthInCtbs}x{s.HeightInCtbs}), min CB {1 << s.Log2MinCodingBlockSize}, TB {1 << s.Log2MinTransformBlockSize}..{1 << s.Log2MaxTransformBlockSize}, " +
                        $"intra TU depth {s.MaxTransformHierarchyDepthIntra}, strong intra smoothing {s.StrongIntraSmoothing}");
        text.AppendLine($"SAO {s.SampleAdaptiveOffset}, scaling lists {s.ScalingListEnabled} (SPS data {s.ScalingListDataInSps}, PPS data {p.ScalingListDataInPps}), PCM {s.PcmEnabled}, AMP {s.AmpEnabled}, " +
                        $"short-term RPS {s.NumShortTermRefPicSets}, long-term {s.LongTermRefPicsPresent}, temporal MVP {s.TemporalMvpEnabled}");
        var range = s.RangeExtension;
        text.AppendLine(range.Any
            ? $"SPS range extension: ts-rotation {range.TransformSkipRotation}, ts-context {range.TransformSkipContext}, implicit RDPCM {range.ImplicitRdpcm}, explicit RDPCM {range.ExplicitRdpcm}, " +
              $"extended precision {range.ExtendedPrecisionProcessing}, intra smoothing disabled {range.IntraSmoothingDisabled}, persistent Rice {range.PersistentRiceAdaptation}, bypass alignment {range.CabacBypassAlignment}"
            : "SPS range extension: none");
        text.AppendLine($"VUI signal: {(s.VideoSignal is { } v ? $"full range {v.FullRange}, primaries {v.Primaries}, transfer {v.Transfer}, matrix {v.Matrix}, chroma location {v.ChromaLocationTop}" : "none")}; " +
                        $"other SPS extensions {s.OtherExtensions}");
        text.AppendLine($"PPS: init QP {p.InitQp}, cb/cr offsets {p.CbQpOffset}/{p.CrQpOffset}, cu_qp_delta {p.CuQpDeltaEnabled} (depth {p.DiffCuQpDeltaDepth}), sign hiding {p.SignDataHiding}, " +
                        $"transform skip {p.TransformSkip} (max {1 << p.Log2MaxTransformSkipBlockSize}), transquant bypass {p.TransquantBypass}, constrained intra {p.ConstrainedIntraPred}");
        text.AppendLine($"PPS: tiles {p.Tiles} ({p.TileColumns}x{p.TileRows}, filter across {p.LoopFilterAcrossTiles}), WPP {p.EntropyCodingSync}, dependent slices {p.DependentSliceSegments}, " +
                        $"filter across slices {p.LoopFilterAcrossSlices}, deblocking disabled {p.DeblockingDisabled} (override {p.DeblockingOverrideEnabled}, beta {p.BetaOffsetDiv2}, tc {p.TcOffsetDiv2})");
        text.AppendLine($"PPS range extension: cross-component {p.CrossComponentPrediction}, chroma QP list {p.ChromaQpOffsetList}, SAO offset scale {p.Log2SaoOffsetScaleLuma}/{p.Log2SaoOffsetScaleChroma}; " +
                        $"other PPS extensions {p.OtherExtensions}, slice header extension {p.SliceHeaderExtensionPresent}, extra header bits {p.NumExtraSliceHeaderBits}");
        text.AppendLine("NAL units in the item: " + string.Join(", ", NalTypeCounts.Select(kv => $"{kv.Key}x{kv.Value}")));
        text.AppendLine("SEI: " + (Sei.Count == 0 ? "none" : string.Join(", ", Sei.Select(m =>
            $"{(m.Suffix ? "suffix" : "prefix")} {m.PayloadType} ({m.PayloadSize} B{(m.HashType is { } h ? $", hash type {h}" : "")})"))));
        text.Append("slices: " + string.Join(", ", Slices.Select(sl =>
            $"[nal {sl.NalType} {(sl.FirstInPicture ? "first" : $"@{sl.SegmentAddress}")}{(sl.Dependent ? " dependent" : $" type {sl.SliceType}")} {sl.Bytes} B]")));
        return text.ToString();
    }

    private static string ProfileName(int idc) => idc switch
    {
        1 => "Main",
        2 => "Main 10",
        3 => "Main Still Picture",
        4 => "Format Range Extensions",
        5 => "High Throughput",
        9 => "Screen Content Coding",
        _ => "other",
    };
}
