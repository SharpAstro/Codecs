using System.Security.Cryptography;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The committed x265 fixtures (<c>Fixtures/hevc</c>, made by <c>Oracle/heif/make-fixtures.sh</c>): one IDR
/// picture each, whose expected output is ffmpeg's decode, committed as a digest only after libde265
/// agreed with it. They exist because the Canon pictures leave most of intra HEVC unexercised (no planar,
/// a quarter of the angles, no 4:2:2 remap that changes anything), so each one is checked to really cover
/// what it is there for, not just to decode right.
/// </summary>
public class HevcFixtureTests(ITestOutputHelper output)
{
    public static TheoryData<string> Names =>
    [
        "x265-422p10-modes",
        "x265-422p10-aq-scaling",
        "x265-420p8-window",
        "x265-420p10-lowqp",
        "x265-420p10-chromaqp-lists",
        "x265-422p12",
        "x265-422p8-ctu16",
        // Rung 4: the loop filters.
        "x265-420p8-deblock",
        "x265-422p10-deblock-offsets",
        "x265-420p10-sao",
        "x265-422p10-loopfilters",
        "x265-420p12-loopfilters",
        "x265-420p8-loopfilters-window",
        "x265-420p8-deblock-highqp",
        "x265-420p8-deblock-highqp-offsets",
        "x265-420p8-deblock-lowqp",
        // Rung 4: sign data hiding and transform skip.
        "x265-420p8-signhide",
        "x265-422p10-signhide-tskip",
        "x265-420p10-tskip-lists",
        "x265-420p12-tskip",
        // Rung 4: 4:4:4 and monochrome.
        "x265-444p8-modes",
        "x265-444p10-lists",
        "x265-444p12-loopfilters",
        "x265-400p8",
        "x265-400p10",
        // Rung 4: wavefronts, slices, lossless coding units.
        "x265-422p10-wpp",
        "x265-420p8-wpp-ctu16",
        "x265-420p8-wpp-slices",
        "x265-422p10-wpp-slices",
        "x265-420p8-lossless",
        "x265-420p10-cu-lossless",
        "x265-420p10-cu-lossless-sao",
    ];

    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hevc");

    [Theory]
    [MemberData(nameof(Names))]
    public void Fixture_DecodesToTheCommittedDigest(string name)
    {
        var (configuration, item) = Load(name);
        var statistics = new HevcDecodeStatistics();
        var picture = HevcDecoder.Decode(configuration, item, statistics);
        Report(name, picture, statistics);

        var golden = Golden()[name];
        var window = picture.ConformanceWindow;
        (picture.Width - window.Left - window.Right, picture.Height - window.Top - window.Bottom).ShouldBe((golden.Width, golden.Height));
        Convert.ToHexStringLower(SHA256.HashData(Serialize(picture))).ShouldBe(golden.Sha256, $"{name} differs from ffmpeg's decode");
    }

    /// <summary>
    /// Each fixture covers what it is there for. A regenerated fixture that quietly stopped using a mode
    /// or a tool would still match its new digest and test nothing; this is what notices.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Fixture_CoversWhatItIsFor(string name)
    {
        var (configuration, item) = Load(name);
        var statistics = new HevcDecodeStatistics();
        var picture = HevcDecoder.Decode(configuration, item, statistics);
        var sps = HevcSps.Parse(HevcNalUnit.Parse(configuration.NalUnitsOfType(HevcNalUnit.SpsNut).Single()).Rbsp);
        var pps = HevcPps.Parse(HevcNalUnit.Parse(configuration.NalUnitsOfType(HevcNalUnit.PpsNut).Single()).Rbsp);

        // Every fixture: all 35 luma modes (planar and all 33 angles included), NxN units, coded 4x4 blocks.
        // Not the deblocking tables' fixtures, whose QPs are pushed to where x265 uses few modes.
        if (!name.StartsWith("x265-420p8-deblock-", StringComparison.Ordinal))
            statistics.LumaModes.ShouldAllBe(count => count > 0, $"{name} leaves a luma mode unused");
        statistics.NxNUnits.ShouldBeGreaterThan(0);
        statistics.CodedTransformSizes[2].ShouldBeGreaterThan(0);
        if (picture.ChromaFormatIdc == 2)
            statistics.RemappedChromaModes.ShouldBeGreaterThan(0, "a 4:2:2 fixture must exercise Table 8-3 with modes it changes");

        switch (name)
        {
            case "x265-422p10-modes":
                (picture.ChromaFormatIdc, picture.BitDepthLuma, sps.Log2CtbSize).ShouldBe((2, 10, 6));
                statistics.CodedTransformSizes[5].ShouldBeGreaterThan(0, "32x32 transforms");
                statistics.StrongSmoothing.ShouldBeGreaterThan(0);
                break;
            case "x265-422p10-aq-scaling":
                statistics.NonZeroQpDeltas.ShouldBeGreaterThan(0);
                (pps.CuQpDeltaEnabled, pps.DiffCuQpDeltaDepth).ShouldBe((true, 1), "16x16 quantization groups in 32x32 CTBs");
                sps.ScalingListEnabled.ShouldBeTrue();
                (pps.CbQpOffset, pps.CrQpOffset).ShouldBe((-3, 2));
                statistics.CodedTransformSizes[5].ShouldBeGreaterThan(0, "the 32x32 scaling list and its DC");
                break;
            case "x265-420p8-window":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((1, 8));
                picture.ConformanceWindow.ShouldNotBe((0, 0, 0, 0));
                sps.StrongIntraSmoothing.ShouldBeFalse();
                (sps.Width % sps.CtbSize, sps.Height % sps.CtbSize).ShouldBe((8, 8), "partial CTBs on the right and bottom");
                break;
            case "x265-420p10-lowqp":
                (picture.ChromaFormatIdc, picture.BitDepthLuma, sps.Log2CtbSize).ShouldBe((1, 10, 4));
                break;
            case "x265-420p10-chromaqp-lists":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((1, 10));
                // Coded lists, with 16x16 and 32x32 DC values a decoder must not take from the list itself.
                var lists = pps.ScalingList ?? sps.ScalingList;
                lists.ShouldNotBeNull("the lists are coded, not the defaults");
                foreach (var (log2, matrix) in new[] { (4, 0), (4, 1), (4, 2), (5, 0) })
                    lists.Factors(log2, matrix)[0].ShouldNotBe(lists.Factors(log2, matrix)[1], $"the {1 << log2}x{1 << log2} DC of list {matrix}");
                statistics.CodedTransformSizes[4].ShouldBeGreaterThan(0);
                statistics.CodedTransformSizes[5].ShouldBeGreaterThan(0);
                (pps.DiffCuQpDeltaDepth, statistics.NonZeroQpDeltas > 0).ShouldBe((2, true), "8x8 quantization groups in 32x32 CTBs");
                // Every qPi Table 8-10 maps (30..43) is used by some coded chroma block.
                var unused = Enumerable.Range(30, 14).Where(qpi => statistics.ChromaQpIndices[qpi + 12] == 0).ToList();
                unused.ShouldBeEmpty("qPi values of the 4:2:0 chroma table no coded block uses");
                break;
            case "x265-422p12":
                (picture.ChromaFormatIdc, picture.BitDepthLuma, picture.BitDepthChroma).ShouldBe((2, 12, 12));
                break;
            case "x265-422p8-ctu16":
                (picture.ChromaFormatIdc, picture.BitDepthLuma, sps.Log2CtbSize).ShouldBe((2, 8, 4));
                break;

            // Rung 4, the loop filters. Each checks the filter it is for really changed samples, in every
            // way the filter can, and that the other filter is off where it should be.
            case "x265-420p8-deblock":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((1, 8));
                ShouldDeblockEveryWay(statistics);
                ShouldHaveNoSao(statistics);
                break;
            case "x265-422p10-deblock-offsets":
                (picture.ChromaFormatIdc, picture.BitDepthLuma, sps.Log2CtbSize).ShouldBe((2, 10, 6));
                (pps.TcOffsetDiv2, pps.BetaOffsetDiv2).ShouldBe((2, -2), "beta and tc offsets");
                (pps.CbQpOffset, pps.CrQpOffset).ShouldBe((-4, 3), "the chroma filter's QP takes the PPS offsets");
                ShouldDeblockEveryWay(statistics);
                ShouldHaveNoSao(statistics);
                break;
            case "x265-420p10-sao":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((1, 10));
                (statistics.DeblockStrong + statistics.DeblockWeak + statistics.DeblockChroma).ShouldBe(0, "deblocking is off");
                ShouldUseSao(statistics);
                statistics.SaoLargestOffset.ShouldBeGreaterThan(7, "an offset past 8-bit's cMax");
                break;
            case "x265-422p10-loopfilters":
                (picture.ChromaFormatIdc, picture.BitDepthLuma, sps.Log2CtbSize).ShouldBe((2, 10, 4));
                ShouldDeblockEveryWay(statistics);
                ShouldUseSao(statistics);
                break;
            case "x265-420p12-loopfilters":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((1, 12));
                ShouldDeblockEveryWay(statistics);
                statistics.SaoEdgeClasses.Sum().ShouldBeGreaterThan(0, "SAO at 12 bits");
                statistics.SaoLargestOffset.ShouldBeGreaterThan(15);
                break;
            case "x265-420p8-deblock-highqp" or "x265-420p8-deblock-highqp-offsets" or "x265-420p8-deblock-lowqp":
                (pps.CuQpDeltaEnabled, statistics.NonZeroQpDeltas > 0).ShouldBe((true, true), "QPs spread by adaptive quantisation");
                ShouldHaveNoSao(statistics);
                break;
            case "x265-420p8-signhide":
                (pps.SignDataHiding, pps.TransformSkip).ShouldBe((true, false));
                ShouldHideSigns(statistics);
                break;
            case "x265-422p10-signhide-tskip":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((2, 10));
                ShouldHideSigns(statistics);
                ShouldSkipTransforms(statistics);
                ShouldUseSao(statistics);
                break;
            case "x265-420p10-tskip-lists":
                sps.ScalingListEnabled.ShouldBeTrue("a skipped 4x4 transform is still weighted by its scaling list");
                (pps.ScalingList ?? sps.ScalingList).ShouldNotBeNull("coded lists, not the flat default");
                ShouldSkipTransforms(statistics);
                break;
            case "x265-420p12-tskip":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((1, 12));
                ShouldSkipTransforms(statistics);
                break;
            case "x265-444p8-modes":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((3, 8));
                statistics.ChromaModesPerPart.ShouldBeGreaterThan(0, "NxN units coding four chroma modes");
                statistics.ChromaModes.Count(c => c > 0).ShouldBeGreaterThan(30, "4:4:4 chroma takes nearly every mode");
                ShouldUseSao(statistics);
                break;
            case "x265-444p10-lists":
                // ffmpeg alone is the reference here: libde265 does not weight 32x32 chroma by the 16x16 list.
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((3, 10));
                (pps.ScalingList ?? sps.ScalingList).ShouldNotBeNull("coded lists");
                statistics.CodedChroma32.ShouldBeGreaterThan(0, "32x32 chroma blocks, weighted by the 16x16 lists");
                break;
            case "x265-444p12-loopfilters":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((3, 12));
                ShouldDeblockEveryWay(statistics);
                ShouldSkipTransforms(statistics);
                break;
            case "x265-400p8" or "x265-400p10":
                picture.ChromaFormatIdc.ShouldBe(0);
                (picture.Cb, picture.Cr).ShouldBe((null, null));
                statistics.ChromaModes.Sum().ShouldBe(0);
                statistics.SaoEdgeClasses.Sum().ShouldBeGreaterThan(0);
                break;
            case "x265-422p10-wpp" or "x265-420p8-wpp-ctu16":
                pps.EntropyCodingSync.ShouldBeTrue();
                statistics.Slices.ShouldBe(1);
                statistics.WavefrontRows.ShouldBe(sps.HeightInCtbs - 1, "every row after the first starts a substream");
                ShouldUseSao(statistics);
                if (name == "x265-422p10-wpp") statistics.NonZeroQpDeltas.ShouldBeGreaterThan(0, "QP prediction restarting at each row");
                break;
            case "x265-420p8-wpp-slices" or "x265-422p10-wpp-slices":
                pps.EntropyCodingSync.ShouldBeTrue();
                statistics.Slices.ShouldBeGreaterThan(2);
                statistics.WavefrontRows.ShouldBeGreaterThan(0, "rows inside a slice, found by entry points");
                statistics.SliceBoundariesKept.ShouldBeGreaterThan(0, "slice_loop_filter_across_slices_enabled_flag 0: edges and SAO neighbours not crossed");
                ShouldDeblockEveryWay(statistics);
                break;
            case "x265-420p10-cu-lossless":
                statistics.BypassUnits.ShouldBeGreaterThan(0, "lossless coding units");
                statistics.FilteringSkippedForBypass.ShouldBeGreaterThan(0, "deblocking edges with a lossless side kept");
                statistics.ChromaDeblockKeptForBypass.ShouldBeGreaterThan(0, "a lossless chroma sample the filter would have changed");
                pps.SignDataHiding.ShouldBeTrue("a lossless unit codes every sign even so");
                break;
            case "x265-420p8-lossless":
                pps.SignDataHiding.ShouldBeTrue("a lossless unit codes every sign even so");
                statistics.BypassUnits.ShouldBeGreaterThan(0);
                break;
            case "x265-420p10-cu-lossless-sao":
                statistics.BypassUnits.ShouldBeGreaterThan(0, "lossless coding units");
                statistics.SaoSamplesKeptForBypass.ShouldBeGreaterThan(0, "SAO leaving a lossless unit's samples alone");
                break;
            case "x265-420p8-loopfilters-window":
                (picture.ChromaFormatIdc, picture.BitDepthLuma).ShouldBe((1, 8));
                picture.ConformanceWindow.ShouldNotBe((0, 0, 0, 0));
                ShouldDeblockEveryWay(statistics);
                ShouldUseSao(statistics);
                statistics.SaoEdgeClasses.ShouldAllBe(count => count > 0, "every edge offset class");
                statistics.SaoLargestOffset.ShouldBe(7, "8-bit's largest offset, cMax");
                break;
        }
    }

    /// <summary>
    /// Table 8-12 entry by entry. A wrong beta' or tC' changes only the edges whose Q selects it, so a
    /// table is checked only as far as the fixtures reach into it (the mutation check found a wrong tC' at
    /// Q 47 that nothing saw). Between them the fixtures must look up every Q where either table is
    /// non-zero. (A SAO band position of 29-31, whose bands wrap past 31 to 0, is in no fixture: x265 never
    /// writes one.)
    /// </summary>
    [Fact]
    public void Fixtures_TogetherReachEveryDeblockingTableEntry()
    {
        var all = new HevcDecodeStatistics();
        foreach (var name in Names.Select(row => (string)row.Data))
        {
            var (configuration, item) = Load(name);
            HevcDecoder.Decode(configuration, item, all);
        }
        Enumerable.Range(16, 36).Where(q => all.BetaIndices[q] == 0).ShouldBeEmpty("beta' entries (Q 16-51) no fixture looks up");
        Enumerable.Range(18, 36).Where(q => all.TcIndices[q] == 0).ShouldBeEmpty("tC' entries (Q 18-53) no fixture looks up");
    }

    private static void ShouldDeblockEveryWay(HevcDecodeStatistics s)
    {
        s.DeblockStrong.ShouldBeGreaterThan(0, "strong luma filtering");
        s.DeblockWeak.ShouldBeGreaterThan(0, "weak luma filtering");
        s.DeblockWeakSecondSample.ShouldBeGreaterThan(0, "weak filtering of p1 or q1 too");
        s.DeblockSkipped.ShouldBeGreaterThan(0, "segments the decisions leave alone");
        s.DeblockChroma.ShouldBeGreaterThan(0, "chroma edges");
    }

    private static void ShouldHideSigns(HevcDecodeStatistics s)
    {
        s.HiddenSignsNegative.ShouldBeGreaterThan(0, "a hidden sign the parity made negative");
        (s.SignsHidden - s.HiddenSignsNegative).ShouldBeGreaterThan(0, "a hidden sign left positive");
    }

    private static void ShouldSkipTransforms(HevcDecodeStatistics s)
    {
        s.TransformSkipLuma.ShouldBeGreaterThan(0, "luma blocks with the transform skipped");
        s.TransformSkipChroma.ShouldBeGreaterThan(0, "chroma blocks with the transform skipped");
    }

    private static void ShouldUseSao(HevcDecodeStatistics s)
    {
        s.SaoBand.ShouldBeGreaterThan(0, "band offset");
        s.SaoEdgeClasses.Sum().ShouldBeGreaterThan(0, "edge offset");
        s.SaoMergeLeft.ShouldBeGreaterThan(0, "parameters merged from the left");
        s.SaoMergeUp.ShouldBeGreaterThan(0, "parameters merged from above");
    }

    private static void ShouldHaveNoSao(HevcDecodeStatistics s) =>
        (s.SaoBand + s.SaoEdgeClasses.Sum() + s.SaoMergeLeft + s.SaoMergeUp).ShouldBe(0, "SAO is off");

    /// <summary>
    /// Damaged slice data is refused with <see cref="InvalidDataException"/> (or decodes to something):
    /// never an index out of range, an overflow, or a hang. Every byte of every slice is overwritten in
    /// turn, plus truncations; the decode is bounded by input size, so each attempt is quick.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Fixture_DamagedSlices_AreRefusedCleanly(string name)
    {
        var (configuration, item) = Load(name);
        var random = new Random(name.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF);
        var outcomes = new Dictionary<string, int>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        void Attempt(byte[] data)
        {
            string outcome;
            try
            {
                HevcDecoder.Decode(configuration, data);
                outcome = "decoded";
            }
            catch (InvalidDataException) { outcome = "invalid"; }
            catch (NotSupportedException) { outcome = "not supported"; }
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        // Skip the 4-byte length and 2-byte NAL header of each unit; damage only what follows.
        for (var i = 0; i < 600; i++)
        {
            var damaged = (byte[])item.Clone();
            var position = 6 + random.Next(item.Length - 6);
            damaged[position] ^= (byte)(1 + random.Next(255));
            Attempt(damaged);
        }
        for (var cut = 16; cut < item.Length; cut += Math.Max(1, item.Length / 50))
        {
            var truncated = item[..cut];
            // Keep the length prefix honest so it is the slice data, not the container framing, that is short.
            var length = cut - 4;
            truncated[0] = (byte)(length >> 24); truncated[1] = (byte)(length >> 16); truncated[2] = (byte)(length >> 8); truncated[3] = (byte)length;
            Attempt(truncated);
        }

        output.WriteLine($"{name}: {string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}"))} in {watch.ElapsedMilliseconds} ms");
        outcomes.GetValueOrDefault("invalid").ShouldBeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Fixture_MatchesLiveFfmpeg(string name)
    {
        var (configuration, item) = Load(name);
        var picture = HevcDecoder.Decode(configuration, item);
        var (luma, cb, cr) = HevcOracle.Decode(HevcOracle.AnnexB(configuration, item), picture);
        var (y, u, v) = Crop(picture);
        y.ShouldBe(luma);
        u.ShouldBe(cb);
        v.ShouldBe(cr);
    }

    private void Report(string name, HevcPicture picture, HevcDecodeStatistics s)
    {
        static string Modes(long[] counts) => string.Join(" ", counts.Select((c, m) => (c, m)).Where(p => p.c > 0).Select(p => $"{p.m}:{p.c}"));
        output.WriteLine($"{name}: {picture.Width}x{picture.Height} chroma {picture.ChromaFormatIdc} {picture.BitDepthLuma}-bit window {picture.ConformanceWindow}");
        output.WriteLine($"  luma modes ({s.LumaModes.Count(c => c > 0)}): {Modes(s.LumaModes)}");
        output.WriteLine($"  chroma modes ({s.ChromaModes.Count(c => c > 0)}): {Modes(s.ChromaModes)}; remapped CUs {s.RemappedChromaModes}");
        output.WriteLine($"  coded TB sizes: {string.Join(" ", s.CodedTransformSizes.Select((c, l) => (c, l)).Where(p => p.c > 0).Select(p => $"{1 << p.l}:{p.c}"))}");
        output.WriteLine($"  NxN {s.NxNUnits}, non-zero QP deltas {s.NonZeroQpDeltas}, strong smoothing {s.StrongSmoothing}, slices {s.Slices}");
        output.WriteLine($"  deblocking: strong {s.DeblockStrong}, weak {s.DeblockWeak} (p1/q1 too {s.DeblockWeakSecondSample}), skipped {s.DeblockSkipped}, chroma {s.DeblockChroma}");
        output.WriteLine($"  beta Q: {string.Join(" ", s.BetaIndices.Select((c, q) => (c, q)).Where(p => p.c > 0).Select(p => p.q))}; tc Q: {string.Join(" ", s.TcIndices.Select((c, q) => (c, q)).Where(p => p.c > 0).Select(p => p.q))}; band positions: {string.Join(" ", s.SaoBandPositions.Select((c, q) => (c, q)).Where(p => p.c > 0).Select(p => p.q))}");
        output.WriteLine($"  wavefront rows {s.WavefrontRows}, lossless CUs {s.BypassUnits} (filtering kept {s.FilteringSkippedForBypass}, chroma {s.ChromaDeblockKeptForBypass}, SAO {s.SaoSamplesKeptForBypass}), slice boundaries kept {s.SliceBoundariesKept}");
        output.WriteLine($"  signs hidden {s.SignsHidden} (negative {s.HiddenSignsNegative}), transform skip luma {s.TransformSkipLuma} chroma {s.TransformSkipChroma}");
        output.WriteLine($"  SAO: band {s.SaoBand}, edge classes {string.Join("/", s.SaoEdgeClasses)}, merged left {s.SaoMergeLeft} up {s.SaoMergeUp}, largest offset {s.SaoLargestOffset}");
    }

    // ---- loading -----------------------------------------------------------------------------------

    /// <summary>Splits an Annex B stream into an hvcC-like configuration and 4-byte length-prefixed item data.</summary>
    internal static (HevcDecoderConfiguration Configuration, byte[] Item) Load(string name) =>
        FromAnnexB(File.ReadAllBytes(Path.Combine(Root, name + ".hevc")));

    /// <summary>An Annex B stream as an hvcC-like configuration and 4-byte length-prefixed item data.</summary>
    internal static (HevcDecoderConfiguration Configuration, byte[] Item) FromAnnexB(byte[] stream)
    {
        var nals = SplitAnnexB(stream);
        var parameterSets = new Dictionary<int, List<byte[]>>();
        var item = new MemoryStream();
        foreach (var nal in nals)
        {
            var type = (nal[0] >> 1) & 0x3F;
            if (type is HevcNalUnit.VpsNut or HevcNalUnit.SpsNut or HevcNalUnit.PpsNut)
            {
                if (!parameterSets.TryGetValue(type, out var list)) parameterSets[type] = list = [];
                list.Add(nal);
                continue;
            }
            item.Write([(byte)(nal.Length >> 24), (byte)(nal.Length >> 16), (byte)(nal.Length >> 8), (byte)nal.Length]);
            item.Write(nal);
        }

        var sps = HevcSps.Parse(HevcNalUnit.Parse(parameterSets[HevcNalUnit.SpsNut][0]).Rbsp);
        var ptl = sps.ProfileTierLevel;
        var configuration = new HevcDecoderConfiguration
        {
            ProfileSpace = ptl.ProfileSpace,
            HighTier = ptl.HighTier,
            ProfileIdc = ptl.ProfileIdc,
            ProfileCompatibilityFlags = ptl.CompatibilityFlags,
            ConstraintIndicatorFlags = ptl.ConstraintFlags,
            LevelIdc = ptl.LevelIdc,
            ChromaFormat = (HevcChromaFormat)sps.ChromaFormatIdc,
            BitDepthLuma = sps.BitDepthLuma,
            BitDepthChroma = sps.BitDepthChroma,
            LengthSize = 4,
            NalUnitArrays = parameterSets.OrderBy(p => p.Key).Select(p => new HevcNalUnitArray(p.Key, true, p.Value)).ToList(),
        };
        return (configuration, item.ToArray());
    }

    private static List<byte[]> SplitAnnexB(byte[] stream)
    {
        // Start codes are 00 00 01 (a leading 00 makes the four-byte form); a NAL unit never contains one.
        var starts = new List<int>();
        for (var i = 0; i + 2 < stream.Length; i++)
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
                starts.Add(i + 3);
        var nals = new List<byte[]>();
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] - 3 : stream.Length;
            while (end > starts[k] && stream[end - 1] == 0) end--; // trailing zeros belong to the next start code
            nals.Add(stream[starts[k]..end]);
        }
        return nals;
    }

    private sealed record GoldenEntry(int Width, int Height, string Format, string Sha256);

    private static Dictionary<string, GoldenEntry> Golden() =>
        File.ReadAllLines(Path.Combine(Root, "hevc-oracle-golden.tsv"))
            .Skip(1)
            .Where(line => line.Length > 0)
            .Select(line => line.Split('\t'))
            .ToDictionary(f => f[0], f => new GoldenEntry(int.Parse(f[1]), int.Parse(f[2]), f[3], f[4]));

    // ---- output in ffmpeg's raw layout ---------------------------------------------------------------

    internal static (ushort[] Y, ushort[] Cb, ushort[] Cr) Crop(HevcPicture picture)
    {
        var w = picture.ConformanceWindow;
        var (subW, subH) = picture.ChromaFormatIdc switch { 1 => (2, 2), 2 => (2, 1), _ => (1, 1) };
        if (picture.Cb is null)
            return (CropPlane(picture.Luma, picture.Width, w.Left, w.Top, picture.Width - w.Left - w.Right, picture.Height - w.Top - w.Bottom), [], []);
        return (
            CropPlane(picture.Luma, picture.Width, w.Left, w.Top, picture.Width - w.Left - w.Right, picture.Height - w.Top - w.Bottom),
            CropPlane(picture.Cb!, picture.ChromaWidth, w.Left / subW, w.Top / subH, (picture.Width - w.Left - w.Right) / subW, (picture.Height - w.Top - w.Bottom) / subH),
            CropPlane(picture.Cr!, picture.ChromaWidth, w.Left / subW, w.Top / subH, (picture.Width - w.Left - w.Right) / subW, (picture.Height - w.Top - w.Bottom) / subH));
    }

    private static ushort[] CropPlane(ushort[] plane, int stride, int left, int top, int width, int height)
    {
        var cropped = new ushort[width * height];
        for (var y = 0; y < height; y++)
            plane.AsSpan((top + y) * stride + left, width).CopyTo(cropped.AsSpan(y * width, width));
        return cropped;
    }

    /// <summary>The cropped planes as ffmpeg's rawvideo writes them: Y, Cb, Cr; one byte a sample at 8 bits, else 16-bit little-endian.</summary>
    private static byte[] Serialize(HevcPicture picture)
    {
        var (y, cb, cr) = Crop(picture);
        var wide = picture.BitDepthLuma > 8;
        var bytes = new MemoryStream();
        foreach (var plane in new[] { y, cb, cr })
            foreach (var sample in plane)
            {
                if (wide) bytes.WriteByte((byte)sample);
                bytes.WriteByte(wide ? (byte)(sample >> 8) : (byte)sample);
            }
        return bytes.ToArray();
    }
}
