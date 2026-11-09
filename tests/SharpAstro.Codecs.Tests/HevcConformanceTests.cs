using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The first picture of JCT-VC conformance bitstreams (H.265.1), fetched by
/// <c>fetch-hevc-conformance.sh</c> and not committed, decoded and compared with ffmpeg's decode of the same
/// picture, sample for sample. They cover what x265 cannot write: tiles, dependent slices, PCM, CRA and BLA
/// pictures, unequal luma and chroma bit depths and the range-extension tools.
/// </summary>
public sealed class HevcConformanceTests(ITestOutputHelper output)
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hevc-conformance");

    internal static string[] Present() =>
        Directory.Exists(Root) ? Directory.GetFiles(Root, "*.bit").Select(Path.GetFileNameWithoutExtension).Order().ToArray()! : [];

    /// <summary>
    /// Streams whose first picture this decoder must decode exactly as ffmpeg does, with what each is for: a
    /// statistic the decode must show, so a stream that stopped exercising its feature is noticed.
    /// </summary>
    public static TheoryData<string, string> Decoded => new()
    {
        { "ADJUST_IPRED_ANGLE_A_RExt_Mitsubishi_2", "4:2:2" },
        { "CONFWIN_A_Sony_1", "window" },
        { "DBLK_A_SONY_3", "deblocking" },
        { "DBLK_B_SONY_3", "deblocking" },
        { "DBLK_D_VIXS_2", "dependent" },
        { "DSLICE_A_HHI_5", "dependent" },
        { "DSLICE_B_HHI_5", "dependent" },
        { "DSLICE_C_HHI_5", "dependent" },
        { "ENTP_C_Qualcomm_1", "" },
        { "EXTPREC_MAIN_444_16_INTRA_8BIT_RExt_Sony_1", "4:4:4" },
        { "GENERAL_10b_422_RExt_Sony_1", "4:2:2" },
        { "GENERAL_12b_400_RExt_Sony_1", "monochrome" },
        { "GENERAL_8b_400_RExt_Sony_1", "monochrome" },
        { "GENERAL_8b_420_RExt_Sony_1", "" },
        { "GENERAL_8b_444_RExt_Sony_2", "4:4:4" },
        { "ipcm_A_NEC_3", "" }, // its first picture has no PCM unit; later ones do
        { "IPCM_A_RExt_NEC_2", "pcm" },
        { "ipcm_B_NEC_3", "pcm" },
        { "ipcm_C_NEC_3", "pcm" },
        { "ipcm_D_NEC_3", "pcm" },
        { "ipcm_E_NEC_2", "pcm" },
        { "NUT_A_ericsson_5", "" },
        { "PICSIZE_C_Bossen_1", "tiles" },
        { "PICSIZE_D_Bossen_1", "" },
        { "RAP_A_docomo_6", "not idr" },
        { "RAP_B_Bossen_2", "" },
        { "SAO_A_MediaTek_4", "sao" },
        { "SAO_H_Parabola_1", "sao" },
        { "SDH_A_Orange_4", "sign hiding" },
        { "SLIST_A_Sony_5", "" },
        { "STRUCT_A_Samsung_7", "" },
        { "TILES_A_Cisco_2", "tiles" },
        { "TILES_B_Cisco_1", "tiles" },
        { "WPP_D_ericsson_MAIN_2", "wavefront" },
    };

    /// <summary>Streams still refused, each by the feature named in the refusal (the rest of rung 4).</summary>
    public static TheoryData<string, string> Refused => new()
    {
        { "Bitdepth_A_RExt_Sony_1", "Luma at 12 bits with chroma at 8" },
        { "Bitdepth_B_RExt_Sony_1", "Luma at 8 bits with chroma at 12" },
        { "CCP_10bit_RExt_QCOM_1", "Transform skip rotation" },
        { "CCP_8bit_RExt_QCOM_1", "Transform skip rotation" },
        { "IPCM_B_RExt_NEC", "Disabled intra smoothing" },
        { "PERSIST_RPARAM_A_RExt_Sony_3", "Transform skip rotation" },
        { "QMATRIX_A_RExt_Sony_1", "Transform skip rotation" },
        { "TSUNEQBD_A_MAIN10_Technicolor_2", "Luma at 10 bits with chroma at 9" },
        { "WAVETILES_RExt_Sony_2", "A bit depth of 16" },
        { "WPP_AND_TILE_AND_CABAC_BYPASS_ALIGN_1_HIGH_TP_444_14BIT_RExt_Apple_2", "Transform skip rotation" },
        { "WPP_HIGH_TP_444_8BIT_RExt_Apple_2", "Transform skip rotation" },
    };

    [Theory]
    [MemberData(nameof(Decoded))]
    public void FirstPicture_DecodesAsFfmpegDoes(string name, string covers)
    {
        var path = Path.Combine(Root, name + ".bit");
        Assert.SkipUnless(File.Exists(path), $"{name} is not present; run tests/SharpAstro.Codecs.Tests/fetch-hevc-conformance.sh");
        var (configuration, item) = FirstPicture(File.ReadAllBytes(path));
        var statistics = new HevcDecodeStatistics();
        var picture = HevcDecoder.Decode(configuration, item, statistics);
        var (y, cb, cr) = HevcFixtureTests.Crop(picture);
        var (fy, fcb, fcr) = HevcOracle.Decode(HevcOracle.AnnexB(configuration, item), picture);
        y.ShouldBe(fy, $"{name}: luma");
        cb.ShouldBe(fcb, $"{name}: Cb");
        cr.ShouldBe(fcr, $"{name}: Cr");

        var firstNal = HevcNalUnit.Parse(HevcNalUnit.SplitLengthPrefixed(item, 4)[0]);
        switch (covers)
        {
            case "pcm": statistics.PcmUnits.ShouldBeGreaterThan(0); break;
            case "tiles": statistics.TileStarts.ShouldBeGreaterThan(0); break;
            case "dependent": statistics.DependentSegments.ShouldBeGreaterThan(0); break;
            case "wavefront": statistics.WavefrontRows.ShouldBeGreaterThan(0); break;
            case "deblocking": (statistics.DeblockStrong + statistics.DeblockWeak).ShouldBeGreaterThan(0); break;
            case "sao": (statistics.SaoBand + statistics.SaoEdgeClasses.Sum()).ShouldBeGreaterThan(0); break;
            case "sign hiding": statistics.SignsHidden.ShouldBeGreaterThan(0); break;
            case "not idr": firstNal.Type.ShouldNotBeOneOf(19, 20); break;
            case "window": picture.ConformanceWindow.ShouldNotBe((0, 0, 0, 0)); break;
            case "monochrome": picture.ChromaFormatIdc.ShouldBe(0); break;
            case "4:2:2": picture.ChromaFormatIdc.ShouldBe(2); break;
            case "4:4:4": picture.ChromaFormatIdc.ShouldBe(3); break;
        }
    }

    /// <summary>
    /// The later IRAP pictures of the decoded streams (up to six each): CRA and BLA pictures in the middle of a
    /// stream decode on their own, and carry reference picture set syntax a first IDR does not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Decoded))]
    public void LaterRandomAccessPictures_DecodeAsFfmpegDoes(string name, string covers)
    {
        _ = covers;
        var path = Path.Combine(Root, name + ".bit");
        Assert.SkipUnless(File.Exists(path), $"{name} is not present; run tests/SharpAstro.Codecs.Tests/fetch-hevc-conformance.sh");
        var pictures = Pictures(File.ReadAllBytes(path), irapOnly: true, limit: 7).Skip(1).ToList();
        Assert.SkipWhen(pictures.Count == 0, $"{name} has one random access picture");
        var types = new List<int>();
        foreach (var (type, configuration, item) in pictures)
        {
            HevcPicture picture;
            try
            {
                picture = HevcDecoder.Decode(configuration, item);
            }
            catch (NotSupportedException e)
            {
                // A later picture may use what is still refused (the range-extension tools); it is not decoded wrong.
                output.WriteLine($"{name}: an IRAP picture of NAL type {type} is refused: {e.Message}");
                continue;
            }
            var (y, cb, cr) = HevcFixtureTests.Crop(picture);
            var (fy, fcb, fcr) = HevcOracle.Decode(HevcOracle.AnnexB(configuration, item), picture);
            (y.SequenceEqual(fy) && cb.SequenceEqual(fcb) && cr.SequenceEqual(fcr)).ShouldBeTrue($"{name}: the IRAP picture of NAL type {type} after {types.Count + 1} others");
            types.Add(type);
        }
        output.WriteLine($"{name}: later IRAP pictures of NAL types {string.Join(" ", types)} match");
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void FirstPicture_IsRefusedByName(string name, string feature)
    {
        var path = Path.Combine(Root, name + ".bit");
        Assert.SkipUnless(File.Exists(path), $"{name} is not present; run tests/SharpAstro.Codecs.Tests/fetch-hevc-conformance.sh");
        var (configuration, item) = FirstPicture(File.ReadAllBytes(path));
        Should.Throw<NotSupportedException>(() => HevcDecoder.Decode(configuration, item)).Message.ShouldContain(feature);
    }

    /// <summary>A stream fetched but in neither list would be tested by nothing; the fetch script and the lists move together.</summary>
    [Fact]
    public void EveryFetchedStream_IsClassified()
    {
        var names = Present();
        Assert.SkipWhen(names.Length == 0, "No conformance streams; run tests/SharpAstro.Codecs.Tests/fetch-hevc-conformance.sh");
        var listed = Decoded.Select(row => (string)row.Data.Item1).Concat(Refused.Select(row => (string)row.Data.Item1)).ToHashSet();
        names.Where(n => !listed.Contains(n)).ShouldBeEmpty();
    }

    private static int Count(ushort[] ours, ushort[] theirs) =>
        ours.Length != theirs.Length ? Math.Max(ours.Length, theirs.Length) : ours.Where((v, i) => v != theirs[i]).Count();

    /// <summary>The first access unit of an Annex B stream (see <see cref="Pictures"/>).</summary>
    internal static (HevcDecoderConfiguration Configuration, byte[] Item) FirstPicture(byte[] stream)
    {
        var first = Pictures(stream, irapOnly: false, limit: 1);
        if (first.Count == 0) throw new InvalidDataException("No slice in the stream.");
        return (first[0].Configuration, first[0].Item);
    }

    /// <summary>
    /// Access units of an Annex B stream as hvcC-like configurations and item data: each picture's slice
    /// segments with the parameter sets its first slice uses (the last of each id before it). With
    /// <paramref name="irapOnly"/>, the IRAP pictures (IDR, CRA, BLA) only, each of which decodes on its own.
    /// </summary>
    internal static List<(int NalType, HevcDecoderConfiguration Configuration, byte[] Item)> Pictures(byte[] stream, bool irapOnly, int limit)
    {
        var vps = new Dictionary<int, byte[]>();
        var sps = new Dictionary<int, (byte[] Nal, HevcSps Parsed)>();
        var pps = new Dictionary<int, (byte[] Nal, HevcPps Parsed)>();
        var pictures = new List<(int, HevcDecoderConfiguration, byte[])>();
        List<byte[]>? slices = null;
        (byte[] Sps, HevcSps Parsed, byte[] Pps, byte[]? Vps) sets = default;

        void Finish()
        {
            if (slices is null) return;
            pictures.Add(Build(slices, sets));
            slices = null;
        }

        foreach (var nal in HevcFixtureTests.SplitAnnexB(stream))
        {
            if (pictures.Count >= limit) break;
            var type = (nal[0] >> 1) & 0x3F;
            var isSlice = type is <= 9 or (>= 16 and <= 21);
            if (!isSlice)
            {
                if (type is HevcNalUnit.VpsNut or HevcNalUnit.SpsNut or HevcNalUnit.PpsNut or HevcNalUnit.AudNut) Finish();
                switch (type)
                {
                    case HevcNalUnit.VpsNut: vps[0] = nal; break;
                    case HevcNalUnit.SpsNut: { var parsedSps = HevcSps.Parse(HevcNalUnit.Parse(nal).Rbsp); sps[parsedSps.Id] = (nal, parsedSps); break; }
                    case HevcNalUnit.PpsNut: { var parsedPps = HevcPps.Parse(HevcNalUnit.Parse(nal).Rbsp); pps[parsedPps.Id] = (nal, parsedPps); break; }
                }
                continue;
            }
            if ((nal[2] & 0x80) != 0) // first_slice_segment_in_pic_flag: a new picture
            {
                Finish();
                if (irapOnly && type is not (>= 16 and <= 23)) continue;
                // slice_pic_parameter_set_id: after the flag and, for an IRAP picture, no_output_of_prior_pics_flag.
                var parsed = HevcNalUnit.Parse(nal);
                var reader = new RbspReader(parsed.Rbsp);
                reader.Flag();
                if (parsed.IsIrap) reader.Flag();
                var (ppsNal, ppsParsed) = pps[reader.Ue(63, "slice_pic_parameter_set_id")];
                var (spsNal, spsParsed) = sps[ppsParsed.SpsId];
                sets = (spsNal, spsParsed, ppsNal, vps.GetValueOrDefault(0));
                slices = [nal];
            }
            else
            {
                slices?.Add(nal);
            }
        }
        if (pictures.Count < limit) Finish();
        return pictures;
    }

    private static (int, HevcDecoderConfiguration, byte[]) Build(List<byte[]> slices, (byte[] Sps, HevcSps Parsed, byte[] Pps, byte[]? Vps) sets)
    {
        var arrays = new List<HevcNalUnitArray>();
        if (sets.Vps is not null) arrays.Add(new HevcNalUnitArray(HevcNalUnit.VpsNut, true, [sets.Vps]));
        arrays.Add(new HevcNalUnitArray(HevcNalUnit.SpsNut, true, [sets.Sps]));
        arrays.Add(new HevcNalUnitArray(HevcNalUnit.PpsNut, true, [sets.Pps]));
        var ptl = sets.Parsed.ProfileTierLevel;
        var configuration = new HevcDecoderConfiguration
        {
            ProfileSpace = ptl.ProfileSpace,
            HighTier = ptl.HighTier,
            ProfileIdc = ptl.ProfileIdc,
            ProfileCompatibilityFlags = ptl.CompatibilityFlags,
            ConstraintIndicatorFlags = ptl.ConstraintFlags,
            LevelIdc = ptl.LevelIdc,
            ChromaFormat = (HevcChromaFormat)sets.Parsed.ChromaFormatIdc,
            BitDepthLuma = sets.Parsed.BitDepthLuma,
            BitDepthChroma = sets.Parsed.BitDepthChroma,
            LengthSize = 4,
            NalUnitArrays = arrays,
        };
        var item = new MemoryStream();
        foreach (var nal in slices)
        {
            item.Write([(byte)(nal.Length >> 24), (byte)(nal.Length >> 16), (byte)(nal.Length >> 8), (byte)nal.Length]);
            item.Write(nal);
        }
        return ((slices[0][0] >> 1) & 0x3F, configuration, item.ToArray());
    }
}
