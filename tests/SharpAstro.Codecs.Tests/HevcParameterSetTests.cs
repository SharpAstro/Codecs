using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The HEVC syntax layer under the HEIF reader (H.265 7.2-7.3, E.2): Exp-Golomb, emulation prevention,
/// and SPS / PPS parsing, against parameter sets written field by field with <see cref="HevcBitWriter"/>.
/// <para>
/// The structures the Canon files leave out (VUI with HRD, inter-predicted short-term RPS, tiles, the
/// range extensions) matter most here: a parser that reads one bit too many or too few in any of them
/// reads every later field shifted, so each test puts a known value AFTER the structure and checks it.
/// </para>
/// </summary>
public class HevcParameterSetTests
{
    [Fact]
    public void ExpGolomb_RoundTripsAcrossItsRange()
    {
        long[] values = [0, 1, 2, 3, 6, 7, 254, 255, 256, 65535, 1L << 20, (1L << 31) - 1, uint.MaxValue - 1];
        var writer = new HevcBitWriter();
        foreach (var v in values) writer.Ue(v);
        foreach (var v in values) writer.Se(v > int.MaxValue ? 1 : (int)v).Se(-(v > int.MaxValue ? 1 : (int)v));
        var rbsp = writer.Rbsp();

        var reader = new RbspReader(rbsp);
        foreach (var v in values) reader.Ue().ShouldBe((uint)v);
        foreach (var v in values)
        {
            var s = v > int.MaxValue ? 1 : (int)v;
            reader.Se().ShouldBe(s);
            reader.Se().ShouldBe(-s);
        }
        reader.MoreRbspData().ShouldBeFalse();
    }

    [Fact]
    public void ExpGolomb_WithMoreThanThirtyOneLeadingZeros_IsRefused()
    {
        var rbsp = new byte[] { 0, 0, 0, 0, 0x80 }; // 32 zeros, then a one
        Should.Throw<InvalidDataException>(() => { var r = new RbspReader(rbsp); r.Ue(); });
    }

    [Fact]
    public void ReadingPastTheEnd_IsRefused_NotZeroFilled()
    {
        Should.Throw<InvalidDataException>(() => { var r = new RbspReader([0xFF]); r.U(9); });
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x00, 0x02 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x03, 0x00, 0x00, 0x03 })]
    [InlineData(new byte[] { 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x34 })]
    public void EmulationPrevention_RoundTrips(byte[] rbsp)
    {
        var nal = HevcBitWriter.ToNal(HevcNalUnit.SpsNut, rbsp);
        // The written payload has no start-code prefix in it...
        for (var i = 2; i + 2 < nal.Length; i++)
            (nal[i] == 0 && nal[i + 1] == 0 && nal[i + 2] <= 2).ShouldBeFalse($"start-code emulation at {i}");
        // ...and parsing gives the RBSP back.
        var parsed = HevcNalUnit.Parse(nal);
        parsed.Type.ShouldBe(HevcNalUnit.SpsNut);
        parsed.Rbsp.ShouldBe(rbsp);
    }

    [Fact]
    public void NalHeader_RejectsForbiddenBitAndZeroTemporalId()
    {
        Should.Throw<InvalidDataException>(() => HevcNalUnit.Parse([0x80 | 33 << 1, 1]));
        Should.Throw<InvalidDataException>(() => HevcNalUnit.Parse([33 << 1, 0]));
    }

    // ---- SPS -------------------------------------------------------------------------------------

    /// <summary>What a test SPS contains; the defaults are the Canon R7 tile's measured configuration.</summary>
    internal sealed record SpsSpec
    {
        public int ChromaFormat { get; init; } = 2;
        public int Width { get; init; } = 1792;
        public int Height { get; init; } = 960;
        public (int L, int R, int T, int B)? Window { get; init; }
        public int BitDepth { get; init; } = 10;
        public int Log2MinCb { get; init; } = 3;
        public int Log2Ctb { get; init; } = 5;
        public int Log2MinTb { get; init; } = 2;
        public int Log2MaxTb { get; init; } = 4;
        public bool ScalingListData { get; init; } = true;
        public bool Sao { get; init; }
        public bool Pcm { get; init; }
        public bool InterPredictedRps { get; init; }
        public bool Vui { get; init; } = true;
        public bool VuiHrd { get; init; }
        public bool RangeExtension { get; init; }
        public int ProfileIdc { get; init; } = 4;
        public int LevelIdc { get; init; } = 153;
    }

    internal static byte[] WriteSps(SpsSpec s)
    {
        var w = new HevcBitWriter();
        w.U(4, 0).U(3, 0).Flag(true);
        w.ProfileTierLevel(s.ProfileIdc, s.LevelIdc, rextConstraints: 0b010100100); // max_10bit, max_422chroma, intra
        w.Ue(0).Ue(s.ChromaFormat);
        if (s.ChromaFormat == 3) w.Flag(false);
        w.Ue(s.Width).Ue(s.Height);
        w.Flag(s.Window is not null);
        if (s.Window is { } win) w.Ue(win.L).Ue(win.R).Ue(win.T).Ue(win.B);
        w.Ue(s.BitDepth - 8).Ue(s.BitDepth - 8);
        w.Ue(4);                         // log2_max_pic_order_cnt_lsb_minus4
        w.Flag(true).Ue(0).Ue(0).Ue(0);  // sub-layer ordering info
        w.Ue(s.Log2MinCb - 3).Ue(s.Log2Ctb - s.Log2MinCb).Ue(s.Log2MinTb - 2).Ue(s.Log2MaxTb - s.Log2MinTb);
        w.Ue(1).Ue(2);                   // transform hierarchy depths
        w.Flag(s.ScalingListData);
        if (s.ScalingListData) { w.Flag(true); WriteScalingList(w); }
        w.Flag(false).Flag(s.Sao).Flag(s.Pcm);
        if (s.Pcm) w.U(4, 7).U(4, 7).Ue(0).Ue(1).Flag(false);

        if (s.InterPredictedRps)
        {
            w.Ue(3);
            w.Ue(2).Ue(1); // set 0 (no prediction flag at index 0): 2 negative, 1 positive
            w.Ue(0).Flag(true).Ue(1).Flag(true).Ue(0).Flag(false);
            // set 1: predicted from set 0 (NumDeltaPocs 3, so 4 entries), using 3 of them.
            w.Flag(true).Flag(false).Ue(0);
            w.Flag(true).Flag(false).Flag(true).Flag(true).Flag(false).Flag(false);
            // set 2: predicted from set 1 (NumDeltaPocs 3 again).
            w.Flag(true).Flag(true).Ue(5);
            w.Flag(false).Flag(false).Flag(false).Flag(false).Flag(false).Flag(false).Flag(true);
        }
        else
        {
            w.Ue(0);
        }

        w.Flag(false);  // long_term_ref_pics_present_flag
        w.Flag(false);  // sps_temporal_mvp_enabled_flag
        w.Flag(false);  // strong_intra_smoothing_enabled_flag

        w.Flag(s.Vui);
        if (s.Vui)
        {
            w.Flag(true).U(8, 255).U(16, 1).U(16, 1); // aspect ratio, extended SAR
            w.Flag(false);                            // overscan
            w.Flag(true).U(3, 5).Flag(true).Flag(true).U(8, 9).U(8, 16).U(8, 9); // signal type, colour description
            w.Flag(true).Ue(2).Ue(2);                 // chroma location
            w.Flag(false).Flag(false).Flag(false);
            w.Flag(true).Ue(1).Ue(2).Ue(3).Ue(4);     // default display window
            w.Flag(s.VuiHrd);
            if (s.VuiHrd)
            {
                w.U(32, 1001).U(32, 60000).Flag(true).Ue(0);
                w.Flag(true);                         // vui_hrd_parameters_present_flag
                w.Flag(true).Flag(true);              // nal and vcl HRD
                w.Flag(true).U(8, 23).U(5, 3).Flag(true).U(5, 4); // sub-picture parameters
                w.U(4, 1).U(4, 2).U(4, 3);
                w.U(5, 23).U(5, 23).U(5, 23);
                w.Flag(false).Flag(false);            // fixed_pic_rate general / within cvs
                w.Flag(false);                        // low_delay_hrd_flag
                w.Ue(1);                              // cpb_cnt_minus1: two entries per HRD
                for (var hrd = 0; hrd < 2; hrd++)
                    for (var j = 0; j < 2; j++) w.Ue(1000 + j).Ue(2000).Ue(3).Ue(4).Flag(j == 1);
            }
            w.Flag(true).Flag(true).Flag(false).Flag(true).Ue(0).Ue(2).Ue(1).Ue(15).Ue(15); // bitstream restriction
        }

        w.Flag(s.RangeExtension);
        if (s.RangeExtension)
        {
            w.Flag(true).Flag(false).Flag(false).Flag(false).U(4, 0);
            // A recognisable pattern across the nine range-extension flags.
            w.Flag(true).Flag(false).Flag(true).Flag(true).Flag(false).Flag(false).Flag(true).Flag(false).Flag(true);
        }

        return w.Nal(HevcNalUnit.SpsNut);
    }

    internal static void WriteScalingList(HevcBitWriter w)
    {
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            for (var matrixId = 0; matrixId < 6; matrixId += sizeId == 3 ? 3 : 1)
            {
                if (matrixId == 0)
                {
                    w.Flag(true); // explicit
                    if (sizeId > 1) w.Se(8);
                    for (var i = 0; i < Math.Min(64, 1 << (4 + (sizeId << 1))); i++) w.Se(i % 3 - 1);
                }
                else
                {
                    w.Flag(false).Ue(1); // copy the previous matrix
                }
            }
        }
    }

    private static HevcSps ParseSps(byte[] nal) => HevcSps.Parse(HevcNalUnit.Parse(nal).Rbsp);

    [Fact]
    public void Sps_TheCanonTileConfiguration_ParsesBack()
    {
        var sps = ParseSps(WriteSps(new SpsSpec()));
        sps.ProfileTierLevel.ProfileIdc.ShouldBe(4);
        sps.ProfileTierLevel.LevelIdc.ShouldBe(153);
        sps.ProfileTierLevel.IntraConstraint.ShouldBeTrue();
        sps.ProfileTierLevel.OnePictureOnly.ShouldBeFalse();
        sps.ChromaFormatIdc.ShouldBe(2);
        (sps.Width, sps.Height).ShouldBe((1792, 960));
        (sps.BitDepthLuma, sps.BitDepthChroma).ShouldBe((10, 10));
        (sps.CtbSize, sps.WidthInCtbs, sps.HeightInCtbs).ShouldBe((32, 56, 30));
        (sps.Log2MinTransformBlockSize, sps.Log2MaxTransformBlockSize).ShouldBe((2, 4));
        (sps.MaxTransformHierarchyDepthInter, sps.MaxTransformHierarchyDepthIntra).ShouldBe((1, 2));
        sps.ScalingListEnabled.ShouldBeTrue();
        sps.ScalingListDataInSps.ShouldBeTrue();
        sps.VideoSignal.ShouldBe(new HevcVideoSignal(5, true, 9, 16, 9, 2));
        sps.RangeExtension.ShouldBe(HevcSpsRangeExtension.None);
    }

    [Theory]
    [InlineData(1, 2, 2)] // 4:2:0: offsets are in units of 2 both ways
    [InlineData(2, 2, 1)] // 4:2:2: horizontally only
    [InlineData(3, 1, 1)] // 4:4:4: luma samples
    public void Sps_ConformanceWindow_IsScaledByTheChromaSubsampling(int chroma, int unitX, int unitY)
    {
        var sps = ParseSps(WriteSps(new SpsSpec { ChromaFormat = chroma, Width = 320, Height = 320, Window = (1, 2, 3, 4) }));
        sps.ConformanceWindow.ShouldBe((1 * unitX, 2 * unitX, 3 * unitY, 4 * unitY));
    }

    [Fact]
    public void Sps_VuiWithHrd_IsReadToTheBit_SoTheRangeExtensionAfterItComesOutRight()
    {
        var sps = ParseSps(WriteSps(new SpsSpec { VuiHrd = true, RangeExtension = true }));
        sps.VideoSignal.ShouldBe(new HevcVideoSignal(5, true, 9, 16, 9, 2));
        sps.RangeExtension.ShouldBe(new HevcSpsRangeExtension(true, false, true, true, false, false, true, false, true));
    }

    [Fact]
    public void Sps_InterPredictedShortTermRps_IsReadToTheBit()
    {
        var sps = ParseSps(WriteSps(new SpsSpec { InterPredictedRps = true, RangeExtension = true }));
        sps.NumShortTermRefPicSets.ShouldBe(3);
        sps.RangeExtension.ShouldBe(new HevcSpsRangeExtension(true, false, true, true, false, false, true, false, true));
    }

    [Fact]
    public void Sps_PcmAndSao_AreReadToTheBit()
    {
        var sps = ParseSps(WriteSps(new SpsSpec { Pcm = true, Sao = true, ScalingListData = false, Vui = false, RangeExtension = true }));
        sps.PcmEnabled.ShouldBeTrue();
        sps.SampleAdaptiveOffset.ShouldBeTrue();
        sps.ScalingListEnabled.ShouldBeFalse();
        sps.VideoSignal.ShouldBeNull();
        sps.RangeExtension.Any.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 960)]     // empty picture
    [InlineData(20000, 960)] // past any HEVC level
    public void Sps_ImpossibleSizes_AreRefused(int width, int height) =>
        Should.Throw<InvalidDataException>(() => ParseSps(WriteSps(new SpsSpec { Width = width, Height = height })));

    [Fact]
    public void Sps_TransformBlocksThatDoNotFitTheCodingBlocks_AreRefused() =>
        Should.Throw<InvalidDataException>(() => ParseSps(WriteSps(new SpsSpec { Log2MinCb = 3, Log2MinTb = 3, Log2MaxTb = 4 })));

    [Fact]
    public void Sps_CutShort_IsRefused()
    {
        var nal = WriteSps(new SpsSpec { VuiHrd = true, RangeExtension = true });
        for (var length = 2; length < nal.Length - 1; length++)
            Should.Throw<InvalidDataException>(() => ParseSps(nal[..length]), $"cut at {length}");
    }

    // ---- PPS -------------------------------------------------------------------------------------

    internal static byte[] WritePps(bool tiles = false, bool deblocking = false, bool rangeExtension = false, bool canon = true)
    {
        var w = new HevcBitWriter();
        w.Ue(0).Ue(0);                         // pps id, sps id
        w.Flag(false).Flag(false).U(3, 0);     // dependent slices, output flag, extra header bits
        w.Flag(false).Flag(false);             // sign hiding, cabac init
        w.Ue(0).Ue(0).Se(0);                   // ref idx defaults, init_qp_minus26
        w.Flag(false);                         // constrained intra
        w.Flag(!canon);                        // transform_skip_enabled_flag
        w.Flag(true).Ue(1);                    // cu_qp_delta, depth 1
        w.Se(-2).Se(3).Flag(false);            // cb/cr offsets, slice chroma offsets
        w.Flag(false).Flag(false).Flag(false); // weighted pred, bipred, transquant bypass
        w.Flag(tiles).Flag(false);
        if (tiles) w.Ue(2).Ue(1).Flag(false).Ue(10).Ue(20).Ue(7).Flag(false); // 3x2 non-uniform, no filter across
        w.Flag(false);                         // loop filter across slices
        w.Flag(true);                          // deblocking_filter_control_present_flag
        w.Flag(false).Flag(!deblocking);
        if (deblocking) w.Se(-3).Se(4);
        w.Flag(false).Flag(false).Ue(0).Flag(false); // scaling list in pps, lists modification, merge level, header extension
        w.Flag(rangeExtension);
        if (rangeExtension)
        {
            w.Flag(true).Flag(false).Flag(false).Flag(false).U(4, 0);
            if (!canon) w.Ue(1);               // log2_max_transform_skip_block_size_minus2
            w.Flag(true);                      // cross-component prediction
            w.Flag(true).Ue(1).Ue(2);          // chroma QP offset list of three
            for (var i = 0; i < 3; i++) w.Se(i - 1).Se(1 - i);
            w.Ue(2).Ue(3);                     // SAO offset scales
        }
        return w.Nal(HevcNalUnit.PpsNut);
    }

    private static HevcPps ParsePps(byte[] nal) => HevcPps.Parse(HevcNalUnit.Parse(nal).Rbsp);

    [Fact]
    public void Pps_TheCanonConfiguration_ParsesBack()
    {
        var pps = ParsePps(WritePps());
        pps.CuQpDeltaEnabled.ShouldBeTrue();
        pps.DiffCuQpDeltaDepth.ShouldBe(1);
        (pps.CbQpOffset, pps.CrQpOffset).ShouldBe((-2, 3));
        pps.DeblockingDisabled.ShouldBeTrue();
        pps.Tiles.ShouldBeFalse();
        pps.TransformSkip.ShouldBeFalse();
        pps.OtherExtensions.ShouldBeFalse();
    }

    [Fact]
    public void Pps_TilesDeblockingAndRangeExtension_AreReadToTheBit()
    {
        var pps = ParsePps(WritePps(tiles: true, deblocking: true, rangeExtension: true, canon: false));
        (pps.TileColumns, pps.TileRows).ShouldBe((3, 2));
        pps.LoopFilterAcrossTiles.ShouldBeFalse();
        pps.DeblockingDisabled.ShouldBeFalse();
        (pps.BetaOffsetDiv2, pps.TcOffsetDiv2).ShouldBe((-3, 4));
        pps.TransformSkip.ShouldBeTrue();
        pps.Log2MaxTransformSkipBlockSize.ShouldBe(3);
        pps.CrossComponentPrediction.ShouldBeTrue();
        pps.ChromaQpOffsetList.ShouldBeTrue();
        (pps.Log2SaoOffsetScaleLuma, pps.Log2SaoOffsetScaleChroma).ShouldBe((2, 3));
    }

    // ---- the survey over a whole coded image ---------------------------------------------------------

    /// <summary>A slice segment NAL unit carrying just the start of its header; the survey reads no further.</summary>
    internal static byte[] WriteSliceStart(bool first, int address, int addressBits, int nalType = 19)
    {
        var w = new HevcBitWriter();
        w.Flag(first);
        w.Flag(false);                 // no_output_of_prior_pics_flag (IRAP)
        w.Ue(0);                       // slice_pic_parameter_set_id
        if (!first) w.U(addressBits, address);
        w.Ue(2);                       // slice_type I
        w.U(16, 0xA5A5);               // stands in for the rest of the header and the slice data
        return w.Nal(nalType);
    }

    internal static byte[] LengthPrefixed(params byte[][] nals) =>
        HeifBuilder.Concat(nals.Select(n => HeifBuilder.Concat(HeifBuilder.U32((uint)n.Length), n)).ToArray());

    private static HevcDecoderConfiguration Configuration(byte[] sps, byte[] pps, int chroma = 2, int depth = 10, int profile = 4, int level = 153) =>
        HeifProperties.ReadHevcConfiguration(HeifBuilder.HvcC(chroma, depth, depth, 4, profile, level,
            (HevcNalUnit.SpsNut, [sps]), (HevcNalUnit.PpsNut, [pps])));

    [Fact]
    public void Survey_ReportsSlicesAndNalTypes()
    {
        // 1792x960 with 32-pixel CTBs is 56x30 = 1680 CTBs: an 11-bit slice_segment_address.
        var data = LengthPrefixed(WriteSliceStart(true, 0, 11), WriteSliceStart(false, 448, 11), WriteSliceStart(false, 896, 11));
        var survey = HevcSurvey.Of(Configuration(WriteSps(new SpsSpec()), WritePps()), data);

        survey.NalTypeCounts.ShouldBe(new Dictionary<int, int> { [19] = 3 });
        survey.Slices.Select(s => (s.FirstInPicture, s.SegmentAddress, s.SliceType)).ShouldBe([(true, 0, 2), (false, 448, 2), (false, 896, 2)]);
        survey.Sei.ShouldBeEmpty();
        survey.Report().ShouldContain("chroma_format_idc 2");
    }

    [Fact]
    public void Survey_ADecodedPictureHashSei_IsReported()
    {
        var sei = HevcBitWriter.ToNal(HevcNalUnit.SuffixSeiNut, [132, 17, 0, .. new byte[16], 0x80]); // MD5 hash, 17 bytes
        var data = LengthPrefixed(WriteSliceStart(true, 0, 11), sei);
        var survey = HevcSurvey.Of(Configuration(WriteSps(new SpsSpec()), WritePps()), data);
        survey.Sei.ShouldBe([new HevcSeiMessage(true, 132, 17, 0)]);
    }

    [Fact]
    public void Survey_AnHvcCThatDisagreesWithItsSps_IsRefused() =>
        Should.Throw<InvalidDataException>(() =>
            HevcSurvey.Of(Configuration(WriteSps(new SpsSpec()), WritePps(), chroma: 1), LengthPrefixed(WriteSliceStart(true, 0, 11))));

    [Fact]
    public void Survey_ASliceAddressPastThePicture_IsRefused() =>
        Should.Throw<InvalidDataException>(() =>
            HevcSurvey.Of(Configuration(WriteSps(new SpsSpec()), WritePps()), LengthPrefixed(WriteSliceStart(true, 0, 11), WriteSliceStart(false, 1680, 11))));

    [Fact]
    public void Survey_ANalUnitRunningPastTheItem_IsRefused()
    {
        var data = LengthPrefixed(WriteSliceStart(true, 0, 11));
        Should.Throw<InvalidDataException>(() => HevcSurvey.Of(Configuration(WriteSps(new SpsSpec()), WritePps()), data[..^1]));
    }
}
