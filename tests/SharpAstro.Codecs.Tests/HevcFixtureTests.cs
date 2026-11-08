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
        }
    }

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
    }

    // ---- loading -----------------------------------------------------------------------------------

    /// <summary>Splits an Annex B stream into an hvcC-like configuration and 4-byte length-prefixed item data.</summary>
    internal static (HevcDecoderConfiguration Configuration, byte[] Item) Load(string name)
    {
        var stream = File.ReadAllBytes(Path.Combine(Root, name + ".hevc"));
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
