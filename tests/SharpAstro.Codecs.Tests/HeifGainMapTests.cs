using SharpAstro.Codecs.Abstractions;
using SharpAstro.GainMap;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using SharpAstro.Jpeg;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// ISO/TS 21496-1 gain maps in HEIF (<c>tmap</c> items): the structure <see cref="HeifFile.GainMapOf"/> reads and the
/// reconstruction <see cref="HeifGainMap"/> does. The files are built with <see cref="HeifBuilder"/> from the committed
/// x265 fixtures (base and map are real HEVC pictures), and the tmap item's data is written by the test-side
/// metadata writer, with the leading version byte the real Samsung file has.
/// <para>
/// What is checked, and against what: W = 0 must be the base linearised EXACTLY (what the format promises); full and
/// partial headroom against <see cref="Expected"/>, a direct per-pixel transcription of the formula in double
/// precision that shares no code with the core (its own sRGB curve, its own bilinear, its own gamma and log2);
/// the base's decode against libheif (Magick.NET) where it can be compared.
/// </para>
/// </summary>
public partial class HeifGainMapTests(ITestOutputHelper output)
{
    private const uint BaseId = 1, MapId = 2, TmapId = 3, GroupId = 50;

    // ---- the metadata the files carry --------------------------------------------------------------

    internal static GainMapMetadata Scalar() => new()
    {
        GainMapMin = 0.5,
        GainMapMax = 6,
        Gamma = 1.4,
        OffsetSdr = 1.0 / 64,
        OffsetHdr = 1.0 / 32,
        HdrCapacityMin = 1,
        HdrCapacityMax = 8,
    };

    private static GainMapMetadata ThreeChannel() => new()
    {
        GainMapMin = 0.5,
        GainMapMax = 2,
        Gamma = 1,
        OffsetSdr = 0.015625,
        OffsetHdr = 0.015625,
        HdrCapacityMin = 1,
        HdrCapacityMax = 16,
        Channels =
        [
            new GainMapChannel(0.5, 2, 1, 0.015625, 0.015625),
            new GainMapChannel(0.25, 4, 1.5, 0.03125, 0.0625),
            new GainMapChannel(1, 8, 0.8, 0, 0.125),
        ],
    };

    /// <summary>The <c>tmap</c> item's data: a version byte (0) and the ISO/TS 21496-1 payload.</summary>
    internal static byte[] TmapData(GainMapMetadata metadata, bool common = true, byte version = 0) =>
        [version, .. Iso21496MetadataTests.Write(metadata, common)];

    // ---- building files ----------------------------------------------------------------------------

    /// <summary>One HEVC image item: a fixture, its displayed size (or a declared one that is not), and an nclx.</summary>
    internal sealed record ImageSpec(string Fixture, byte[]? Nclx = null, (uint W, uint H)? DeclaredSize = null)
    {
        /// <summary>A 3 x 2 grid of the same fixture's pictures, output as large as the tiles together.</summary>
        public bool Grid { get; init; }
    }

    private static byte[] Srgb(bool full = true) => HeifBuilder.Nclx(1, 13, 1, full);

    private static (uint W, uint H) Displayed(string fixture)
    {
        var (configuration, item) = HevcFixtureTests.Load(fixture);
        var picture = HevcDecoder.Decode(configuration, item);
        var window = picture.ConformanceWindow;
        return ((uint)(picture.Width - window.Left - window.Right), (uint)(picture.Height - window.Top - window.Bottom));
    }

    /// <summary>Adds an image item (single or grid) with id <paramref name="id"/> and its properties.</summary>
    private static void AddImage(HeifBuilder builder, uint id, ImageSpec spec, bool hidden, uint firstTileId)
    {
        var (configuration, data) = HevcFixtureTests.Load(spec.Fixture);
        var (w, h) = Displayed(spec.Fixture);
        var hvcc = HeifDecodeTests.HvcC(configuration);
        if (!spec.Grid)
        {
            builder.AddItem(new HeifBuilder.Item(id, "hvc1", data, hidden));
            builder.Associate(id, "hvcC", hvcc, essential: true);
            var (dw, dh) = spec.DeclaredSize ?? (w, h);
            builder.Associate(id, "ispe", HeifBuilder.Ispe(dw, dh));
            if (spec.Nclx is not null) builder.Associate(id, "colr", spec.Nclx);
            return;
        }

        const int columns = 3, rows = 2;
        builder.AddItem(new HeifBuilder.Item(id, "grid", HeifBuilder.GridLayout(rows, columns, w * columns, h * rows), hidden));
        var tiles = Enumerable.Range((int)firstTileId, columns * rows).Select(i => (uint)i).ToArray();
        var hvccIndex = builder.Property("hvcC", hvcc);
        var ispeIndex = builder.Property("ispe", HeifBuilder.Ispe(w, h));
        foreach (var tile in tiles)
        {
            builder.AddItem(new HeifBuilder.Item(tile, "hvc1", data, Hidden: true));
            builder.Associate(tile, hvccIndex, essential: true);
            builder.Associate(tile, ispeIndex);
        }
        builder.Reference("dimg", id, tiles);
        var (gw, gh) = spec.DeclaredSize ?? (w * columns, h * rows);
        builder.Associate(id, "ispe", HeifBuilder.Ispe(gw, gh));
        if (spec.Nclx is not null) builder.Associate(id, "colr", spec.Nclx);
    }

    internal sealed class FileOptions
    {
        public bool TmapIsPrimary { get; set; }
        public byte[]? TmapBytes { get; set; }
        public uint[]? Dimg { get; set; }
        public bool Altr { get; set; } = true;
        public byte[]? TmapIrot { get; set; }
        /// <summary>More transformative properties on the tmap, in order, after <see cref="TmapIrot"/>: (type, body).</summary>
        public List<(string Type, byte[] Body)> TmapProperties { get; } = [];
        /// <summary>An <c>irot</c> body on the gain-map image itself (a map stored in another orientation).</summary>
        public byte[]? MapIrot { get; set; }
        /// <summary>An <c>irot</c> body on the base image itself.</summary>
        public byte[]? BaseIrot { get; set; }
        public bool NoTmap { get; set; }
    }

    /// <summary>
    /// A gain-map file. Layout A (default): the base is the primary, the tmap beside it in an altr group with it.
    /// Layout B: the tmap is the primary and the base is hidden, in the altr group.
    /// </summary>
    internal static byte[] Build(ImageSpec baseSpec, ImageSpec mapSpec, GainMapMetadata? metadata = null, Action<FileOptions>? configure = null)
    {
        var options = new FileOptions();
        configure?.Invoke(options);
        var builder = new HeifBuilder { PrimaryItemId = options.TmapIsPrimary ? TmapId : BaseId, IlocVersion = 1 };
        AddImage(builder, BaseId, baseSpec, hidden: options.TmapIsPrimary, firstTileId: 10);
        AddImage(builder, MapId, mapSpec, hidden: true, firstTileId: 20);
        if (options.BaseIrot is not null) builder.Associate(BaseId, "irot", options.BaseIrot, essential: true);
        if (options.MapIrot is not null) builder.Associate(MapId, "irot", options.MapIrot, essential: true);
        if (!options.NoTmap)
        {
            builder.AddItem(new HeifBuilder.Item(TmapId, "tmap", options.TmapBytes ?? TmapData(metadata ?? Scalar())));
            builder.Reference("dimg", TmapId, options.Dimg ?? [BaseId, MapId]);
            var (bw, bh) = baseSpec.DeclaredSize ?? (baseSpec.Grid ? (Displayed(baseSpec.Fixture).W * 3, Displayed(baseSpec.Fixture).H * 2) : Displayed(baseSpec.Fixture));
            builder.Associate(TmapId, "ispe", HeifBuilder.Ispe(bw, bh));
            if (options.TmapIrot is not null) builder.Associate(TmapId, "irot", options.TmapIrot, essential: true);
            foreach (var (type, body) in options.TmapProperties) builder.Associate(TmapId, type, body, essential: type != "imir");
            if (options.Altr) builder.EntityGroup("altr", GroupId, TmapId, BaseId);
        }
        return builder.Build();
    }

    internal static readonly ImageSpec Base256 = new("x265-420p8-deblock", Srgb());                // 256x192, 8-bit 4:2:0
    private static readonly ImageSpec BaseGrid = new("x265-420p8-window", Srgb()) { Grid = true }; // 690x332 grid of 230x166
    internal static readonly ImageSpec Mono8 = new("x265-400p8", HeifBuilder.Nclx(2, 2, 2, true));  // 256x192, 4:0:0, nclx all unspecified, as Samsung's
    private static readonly ImageSpec Mono10 = new("x265-400p10", HeifBuilder.Nclx(2, 2, 2, true)); // the same at 10 bits
    private static readonly ImageSpec Colour10 = new("x265-420p10-lowqp", HeifBuilder.Nclx(1, 13, 1, true)); // 128x96, 4:2:0, 10 bits

    // ---- the structure -----------------------------------------------------------------------------

    [Fact]
    public void BaseIsPrimary_TheTmapSitsBesideIt_InAnAltrGroup()
    {
        var file = Build(Base256, Mono8);
        var heif = HeifReader.Read(file);

        var info = heif.GainMapOf(file, heif.PrimaryItem).ShouldNotBeNull();
        info.Layout.ShouldBe(HeifGainMapLayout.BaseIsPrimary);
        (info.Tmap!.Id, info.Base.Id, info.Map.Id).ShouldBe((TmapId, BaseId, MapId));
        var group = info.Alternatives.ShouldNotBeNull();
        (group.Type, group.GroupId).ShouldBe(("altr", GroupId));
        group.EntityIds.ShouldBe([TmapId, BaseId], "the group is read, with its order");
        info.Metadata.ChannelCount.ShouldBe(1);
        info.Metadata.GetChannel(0).Gamma.ShouldBe(1.4, 1e-5);
        info.Metadata.HdrCapacityMax.ShouldBe(8, 1e-5);

        // From the tmap itself: the same answer. From the map, a hidden gain-map image: none.
        heif.GainMapOf(file, heif.GetItem(TmapId)!).ShouldNotBeNull().Base.Id.ShouldBe(BaseId);
        heif.GainMapOf(file, heif.GetItem(MapId)!).ShouldBeNull();
        heif.GainMapOfPrimary(file).ShouldNotBeNull().Tmap!.Id.ShouldBe(TmapId);
    }

    [Fact]
    public void TmapIsPrimary_TheBaseIsHiddenInTheAltrGroup()
    {
        var file = Build(Base256, Mono8, configure: o => o.TmapIsPrimary = true);
        var heif = HeifReader.Read(file);
        heif.PrimaryItemId.ShouldBe(TmapId);
        heif.GetItem(BaseId)!.Hidden.ShouldBeTrue();

        var info = heif.GainMapOf(file, heif.PrimaryItem).ShouldNotBeNull();
        info.Layout.ShouldBe(HeifGainMapLayout.TmapIsPrimary);
        (info.Base.Id, info.Map.Id).ShouldBe((BaseId, MapId));
        info.Alternatives.ShouldNotBeNull().EntityIds.ShouldBe([TmapId, BaseId]);
        // The base as a plain item finds the same tmap.
        heif.GainMapOf(file, heif.GetItem(BaseId)!).ShouldNotBeNull().Tmap!.Id.ShouldBe(TmapId);

        // And the whole decode starts from the primary, which is the tmap.
        var hdr = HeifGainMap.Decode(file);
        (hdr.Width, hdr.Height).ShouldBe((256, 192));
    }

    [Fact]
    public void WithoutAnAltrGroup_TheTmapIsStillFound_AndReportsNone()
    {
        var file = Build(Base256, Mono8, configure: o => o.Altr = false);
        var heif = HeifReader.Read(file);
        heif.EntityGroups.ShouldBeEmpty();
        heif.GainMapOfPrimary(file).ShouldNotBeNull().Alternatives.ShouldBeNull();
    }

    [Fact]
    public void AFileWithNoTmap_HasNoGainMap()
    {
        var file = Build(Base256, Mono8, configure: o => o.NoTmap = true);
        var heif = HeifReader.Read(file);
        heif.GainMapOfPrimary(file).ShouldBeNull();
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file)).Message.ShouldContain("no gain map");
    }

    /// <summary>
    /// The real file the layout and the version byte come from (fetched, as the other phone files are; skipped without it):
    /// Samsung's tmap 38 over the grid 33 and the gain-map grid 37, in the altr group 42 with the base.
    /// </summary>
    [Fact]
    public void Samsung_RealFile_IsReadAndReconstructed()
    {
        var file = HeifCanonFileTests.Load("samsung-tmap-aux-ycbcr.heic");
        var heif = HeifReader.Read(file);
        var info = heif.GainMapOfPrimary(file).ShouldNotBeNull();
        info.Layout.ShouldBe(HeifGainMapLayout.BaseIsPrimary);
        (info.Tmap!.Id, info.Base.Id, info.Map.Id).ShouldBe((38u, 33u, 37u));
        var group = info.Alternatives.ShouldNotBeNull();
        group.GroupId.ShouldBe(42u);
        group.EntityIds.ShouldBe([38u, 33u]);
        info.Metadata.ChannelCount.ShouldBe(1);
        info.Metadata.HdrCapacityMin.ShouldBe(1, 1e-9);
        info.Metadata.HdrCapacityMax.ShouldBe(Math.Pow(2, 9646899.0 / 4194304), 1e-6);   // 2.3 stops
        var channel = info.Metadata.GetChannel(0);
        (channel.GainMapMin, channel.Gamma, channel.OffsetSdr, channel.OffsetHdr).ShouldBe((1.0, 1.0, 0.0, 0.0));
        channel.GainMapMax.ShouldBe(info.Metadata.HdrCapacityMax, 1e-9);

        var hdr = HeifGainMap.Decode(file);
        output.WriteLine($"{hdr.Width}x{hdr.Height}, headroom {hdr.DisplayHeadroom:0.00}, peak {hdr.Rgba.Where((_, i) => i % 4 != 3).Max():0.000}");
        (hdr.Width, hdr.Height).ShouldBe((4000, 1848));
        hdr.Rgba.Where((_, i) => i % 4 != 3).Max().ShouldBeGreaterThan(1f);
    }

    // ---- exact: W = 0 is the base, linearised ------------------------------------------------------

    [Theory]
    [InlineData(13)]  // sRGB
    [InlineData(1)]   // BT.709
    [InlineData(16)]  // PQ: 1.0 = 203 cd/m2 on this scale
    public void SdrHeadroom_IsTheLinearisedBase_Exactly(int transfer)
    {
        var file = Build(Base256 with { Nclx = HeifBuilder.Nclx(1, transfer, 1, true) }, Mono8);
        var hdr = HeifGainMap.Decode(file, displayHeadroom: 1);
        var heif = HeifReader.Read(file);
        var expected = HeifDecoder.Decode(file, heif.GetItem(BaseId)).ToLinearRgbaFloat(out var encoding);

        hdr.Weight.ShouldBe(0);
        hdr.Rgba.ShouldBe(expected, "bit for bit: no +offset - offset float noise at SDR headroom");
        hdr.ColorEncoding.ShouldBe(encoding with { Float = FloatSemantics.DisplayReferred });
    }

    [Fact]
    public void AnUnspecifiedBaseTransfer_IsSrgb()
    {
        var plain = Build(Base256, Mono8);
        var unspecified = Build(Base256 with { Nclx = HeifBuilder.Nclx(1, 2, 1, true) }, Mono8);
        HeifGainMap.Decode(unspecified, displayHeadroom: 1).Rgba.ShouldBe(HeifGainMap.Decode(plain, displayHeadroom: 1).Rgba);
    }

    [Fact]
    public void TheEncoding_IsLinear_WithTheBasesPrimaries_AndAlphaIsOne()
    {
        var file = Build(Base256 with { Nclx = HeifBuilder.Nclx(9, 13, 9, true) }, Mono8);
        var hdr = HeifGainMap.Decode(file);
        hdr.ColorEncoding.ShouldBe(new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Linear, FullRange = true, Float = FloatSemantics.DisplayReferred });
        hdr.Rgba.Length.ShouldBe(hdr.Width * hdr.Height * 4);
        for (var p = 0; p < hdr.Width * hdr.Height; p += 7) hdr.Rgba[4 * p + 3].ShouldBe(1f);
    }

    // ---- against a transcription of the formula ----------------------------------------------------

    /// <summary>The cases: (name, base, map, metadata, map channels the reconstruction should use).</summary>
    public static TheoryData<string> Cases => ["mono8-same-size", "mono8-grid-base-upsampled", "mono10-codes", "colour10-three-channel", "colour10-one-channel-metadata"];

    private static (byte[] File, int MapChannels) CaseFile(string name) => name switch
    {
        "mono8-same-size" => (Build(Base256, Mono8, Scalar()), 1),
        "mono8-grid-base-upsampled" => (Build(BaseGrid, Mono8, Scalar()), 1),       // 256x192 map over 690x332: 2.7x, 2.6x
        "mono10-codes" => (Build(Base256, Mono10, Scalar()), 1),                    // 1023, not 255, is full scale
        "colour10-three-channel" => (Build(Base256, Colour10, ThreeChannel()), 3), // 128x96 colour map, per-channel parameters
        "colour10-one-channel-metadata" => (Build(Base256, Colour10, Scalar()), 1),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void FullHeadroom_MatchesTheFormula_PixelForPixel(string name)
    {
        var (file, mapChannels) = CaseFile(name);
        var hdr = HeifGainMap.Decode(file);
        hdr.Weight.ShouldBe(1);
        hdr.DisplayHeadroom.ShouldBe(hdr.Metadata.HdrCapacityMax, "the default is the full authored headroom");
        AssertMatchesFormula(file, hdr, hdr.Metadata.HdrCapacityMax, mapChannels, name);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PartialHeadroom_MatchesTheFormula_PixelForPixel(string name)
    {
        var (file, mapChannels) = CaseFile(name);
        var hdr = HeifGainMap.Decode(file, displayHeadroom: 2);   // between SDR (1) and the authored 8 or 16
        hdr.DisplayHeadroom.ShouldBe(2);
        hdr.Weight.ShouldBeInRange(0.01, 0.99);
        AssertMatchesFormula(file, hdr, 2, mapChannels, name);
    }

    [Fact]
    public void AboveTheAuthoredHeadroom_AppliesNoMoreThanTheFullGain()
    {
        var file = Build(Base256, Mono8);
        HeifGainMap.Decode(file, displayHeadroom: 1000).Rgba.ShouldBe(HeifGainMap.Decode(file).Rgba);
    }

    [Fact]
    public void TheGain_ActuallyBoostsAndAttenuates()
    {
        // A guard against a transcription that agrees with a reconstruction doing nothing: the maximum of the
        // HDR result is above SDR white and some pixel is darker than its base.
        var file = Build(Base256, Mono8, Scalar());
        var hdr = HeifGainMap.Decode(file);
        var sdr = HeifGainMap.Decode(file, displayHeadroom: 1);
        hdr.Rgba.Where((_, i) => i % 4 != 3).Max().ShouldBeGreaterThan(1.5f);
        Enumerable.Range(0, hdr.Width * hdr.Height).Count(p => hdr.Rgba[4 * p + 1] < sdr.Rgba[4 * p + 1] * 0.9f).ShouldBeGreaterThan(100);
    }

    private void AssertMatchesFormula(byte[] file, HeifGainMapImage hdr, double headroom, int mapChannels, string what)
    {
        var heif = HeifReader.Read(file);
        var info = heif.GainMapOfPrimary(file)!;
        var baseRgb = HeifDecoder.Decode(file, info.Base);
        var mapRgb = HeifDecoder.Decode(file, info.Map);
        (hdr.Width, hdr.Height).ShouldBe((baseRgb.Width, baseRgb.Height));
        var expected = Expected(baseRgb, mapRgb, info.Metadata, headroom, mapChannels);

        var worst = 0.0;
        var at = -1;
        for (var p = 0; p < baseRgb.Width * baseRgb.Height; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                var diff = Math.Abs(hdr.Rgba[4 * p + c] - expected[3 * p + c]) / Math.Max(1.0, Math.Abs(expected[3 * p + c]));
                if (diff > worst) (worst, at) = (diff, 4 * p + c);
            }
        }
        output.WriteLine($"{what}: {baseRgb.Width}x{baseRgb.Height} base, {mapRgb.Width}x{mapRgb.Height} map, headroom {headroom}: worst relative difference {worst:E2}");
        worst.ShouldBeLessThan(2e-5, $"{what}: first worst sample {at}");
    }

    /// <summary>
    /// The formula, written out per pixel in double: sRGB EOTF of the base code, the map's code over ITS maximum,
    /// centre-aligned bilinear upsampling, recovery^(1/gamma), the log-space lerp scaled by the weight, then
    /// (base + offsetSdr) * 2^gain - offsetHdr. Nothing here is the core's code.
    /// </summary>
    private static double[] Expected(HeifRgbImage baseRgb, HeifRgbImage mapRgb, GainMapMetadata m, double headroom, int mapChannels)
    {
        var (w, h, mw, mh) = (baseRgb.Width, baseRgb.Height, mapRgb.Width, mapRgb.Height);
        var baseMax = (1 << baseRgb.BitDepth) - 1.0;
        var mapMax = (1 << mapRgb.BitDepth) - 1.0;
        var weight = Math.Clamp((Math.Log2(headroom) - Math.Log2(m.HdrCapacityMin)) / (Math.Log2(m.HdrCapacityMax) - Math.Log2(m.HdrCapacityMin)), 0, 1);

        static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double MapAt(int x, int y, int channel) => mapRgb.Samples[3 * (y * mw + x) + (mapChannels == 1 ? 1 : channel)] / mapMax;

        var result = new double[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            var fy = (y + 0.5) * mh / h - 0.5;
            var y0 = (int)Math.Floor(fy);
            var ty = fy - y0;
            int ya = Math.Clamp(y0, 0, mh - 1), yb = Math.Clamp(y0 + 1, 0, mh - 1);
            ty = Math.Clamp(ty, 0, 1);
            for (var x = 0; x < w; x++)
            {
                var fx = (x + 0.5) * mw / w - 0.5;
                var x0 = (int)Math.Floor(fx);
                var tx = Math.Clamp(fx - x0, 0, 1);
                int xa = Math.Clamp(x0, 0, mw - 1), xb = Math.Clamp(x0 + 1, 0, mw - 1);
                for (var c = 0; c < 3; c++)
                {
                    var channel = m.GetChannel(c);
                    var top = MapAt(xa, ya, c) * (1 - tx) + MapAt(xb, ya, c) * tx;
                    var bottom = MapAt(xa, yb, c) * (1 - tx) + MapAt(xb, yb, c) * tx;
                    var recovery = top * (1 - ty) + bottom * ty;
                    recovery = Math.Pow(recovery, 1 / channel.Gamma);
                    var logGain = Math.Log2(channel.GainMapMin) + (Math.Log2(channel.GainMapMax) - Math.Log2(channel.GainMapMin)) * recovery;
                    var sdr = SrgbToLinear(baseRgb.Samples[3 * (y * w + x) + c] / baseMax);
                    result[3 * (y * w + x) + c] = (sdr + channel.OffsetSdr) * Math.Pow(2, logGain * weight) - channel.OffsetHdr;
                }
            }
        }
        return result;
    }

    // ---- the tmap's own transformative properties --------------------------------------------------

    [Fact]
    public void TheTmapsIrot_RotatesTheReconstruction()
    {
        var plain = HeifGainMap.Decode(Build(Base256, Mono8));
        var rotated = HeifGainMap.Decode(Build(Base256, Mono8, configure: o => o.TmapIrot = HeifBuilder.Irot(90)));
        (rotated.Width, rotated.Height).ShouldBe((plain.Height, plain.Width));
        // 90 degrees anticlockwise: (x, y) -> (y, width - 1 - x).
        for (var y = 0; y < plain.Height; y += 5)
        {
            for (var x = 0; x < plain.Width; x += 5)
            {
                var from = 4 * (y * plain.Width + x);
                var to = 4 * ((plain.Width - 1 - x) * plain.Height + y);
                for (var c = 0; c < 4; c++) rotated.Rgba[to + c].ShouldBe(plain.Rgba[from + c]);
            }
        }
    }

    // ---- the map's shape ---------------------------------------------------------------------------

    [Fact]
    public void AMapInAnotherOrientationThanItsBase_IsRefused_NotStretched()
    {
        // The same 256x192 map, turned to 192x256 by its own irot: landscape base, portrait map.
        var file = Build(Base256, Mono8, configure: o => o.MapIrot = HeifBuilder.Irot(90));
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file)).Message.ShouldContain("orientation");

        // The facade does not fail on it: the file is the plain base.
        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(256, 192, 3, SampleFormat.UInt16));
        ImageCodecs.TryDecode(file, out var image).ShouldBeTrue();
        image.ShouldBeOfType<RasterImage>();
    }

    [Fact]
    public void AMapTurnedTheSameWayAsItsBase_IsAccepted()
    {
        // Both turned by their own irot: portrait base, portrait map. The check is on the shape the pictures end up with.
        var file = Build(Base256, Mono8, configure: o => { o.MapIrot = HeifBuilder.Irot(90); o.BaseIrot = HeifBuilder.Irot(90); });
        var image = HeifGainMap.Decode(file);
        (image.Width, image.Height).ShouldBe((192, 256));
    }

    [Fact]
    public void ATmapsMapWithNoIrotOnARotatedBase_IsInTheBasesDisplayedOrientation_AndReconstructs()
    {
        // In a derived image the inputs are the items as displayed: the base (256x192 stored, irot 90) shows 192x256, and
        // a 192x256 map with no irot is already that orientation. It is not an Apple sensor-orientation map, so it is not turned.
        // A really portrait-coded map is needed (the decode checks the coded size): the tall fixture, a 4:2:0 map the
        // single-channel metadata reads as one channel. It is bigger than the 256x192 base, so the base is the 690x332 grid.
        var tall = new ImageSpec("x265-420p8-wpp-tall-flat", HeifBuilder.Nclx(2, 2, 2, true));
        var (mapW, mapH) = Displayed(tall.Fixture);
        mapH.ShouldBeGreaterThan(mapW);
        (mapW * mapH).ShouldBeLessThanOrEqualTo(690u * 332u);
        var file = Build(BaseGrid, tall, configure: o => o.BaseIrot = HeifBuilder.Irot(90));
        var heif = HeifReader.Read(file);
        var info = heif.GainMapOfPrimary(file)!;
        info.Dialect.ShouldBe(HeifGainMapDialect.IsoTmap);
        HeifGainMap.MapMatchesBaseShape(heif.Describe(file, info.Base), heif.Describe(file, info.Map), info.Dialect).ShouldBeTrue();

        var image = HeifGainMap.Decode(file);
        (image.Width, image.Height).ShouldBe((332, 690));
        ImageCodecs.TryReadInfo(file, out var read).ShouldBeTrue();
        read.ShouldBe(new ImageInfo(332, 690, 4, SampleFormat.Float32));
    }

    [Fact]
    public void ATmapsMapStoredLandscapeWithNoIrotOnARotatedBase_IsRefused()
    {
        // The tmap dialect compares display sizes: a landscape map under a base displayed as portrait is the other orientation
        // (only Apple's auxiliary map is taken to be stored like the base).
        var file = Build(Base256, Mono8, configure: o => o.BaseIrot = HeifBuilder.Irot(90));
        var heif = HeifReader.Read(file);
        var info = heif.GainMapOfPrimary(file)!;
        info.Dialect.ShouldBe(HeifGainMapDialect.IsoTmap);
        HeifGainMap.MapMatchesBaseShape(heif.Describe(file, info.Base), heif.Describe(file, info.Map), info.Dialect).ShouldBeFalse();
    }

    [Theory]
    [InlineData(3001, 3000, 750, 751, true)]   // a pixel either side of square
    [InlineData(3000, 3001, 751, 750, true)]
    [InlineData(3002, 3000, 750, 752, true)]   // two pixels
    [InlineData(3000, 3000, 750, 752, true)]
    [InlineData(3010, 3000, 750, 760, false)]  // a truly opposite orientation
    [InlineData(4000, 3000, 750, 1000, false)]
    [InlineData(4000, 3000, 1000, 750, true)]
    public void TheShapeCheck_ToleratesAPixelOrTwoAtTheSquareBoundary(uint baseW, uint baseH, uint mapW, uint mapH, bool accepted)
    {
        var file = Build(new ImageSpec("x265-420p8-deblock", Srgb(), (baseW, baseH)), new ImageSpec("x265-400p8", HeifBuilder.Nclx(2, 2, 2, true), (mapW, mapH)));
        var heif = HeifReader.Read(file);
        HeifGainMap.MapMatchesBaseShape(heif.Describe(file, heif.GetItem(BaseId)!), heif.Describe(file, heif.GetItem(MapId)!), HeifGainMapDialect.IsoTmap).ShouldBe(accepted);
    }

    // ---- libheif on the base -----------------------------------------------------------------------

    /// <summary>
    /// libheif (Magick.NET) decodes the primary image of the file, which is the base: ours must be where the
    /// 4:2:0 8-bit comparison lies elsewhere in the suite (at most 2 codes, on a small share of the samples).
    /// </summary>
    [Fact]
    public void TheBase_MatchesLibheif()
    {
        var file = Build(Base256, Mono8);
        var baseRgb = HeifDecoder.Decode(file, HeifReader.Read(file).GetItem(BaseId));
        var (differing, max, _, total) = HeifDecodeTests.MeasureAgainstLibheif("gain-map base", file, baseRgb, sameSize: true);
        output.WriteLine($"base: {differing} of {total} samples differ from libheif, max {max}");
        max.ShouldBeLessThanOrEqualTo(2);
        differing.ShouldBeLessThan((long)(0.5 * total));
    }

    // ---- refusals ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AWrongNumberOfInputs_IsInvalidData(int count)
    {
        var dimg = count == 1 ? new[] { BaseId } : [BaseId, MapId, BaseId];
        var file = Build(Base256, Mono8, configure: o => o.Dimg = dimg);
        var heif = HeifReader.Read(file);
        Should.Throw<InvalidDataException>(() => heif.GainMapOfPrimary(file)).Message.ShouldContain("exactly two");
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file));
    }

    [Fact]
    public void AMissingInput_IsRefusedWhenTheFileIsRead()
    {
        var file = Build(Base256, Mono8, configure: o => o.Dimg = [BaseId, 99]);
        Should.Throw<InvalidDataException>(() => HeifReader.Read(file)).Message.ShouldContain("99");
    }

    [Fact]
    public void TheSameItemAsBaseAndMap_IsInvalidData()
    {
        var file = Build(Base256, Mono8, configure: o => o.Dimg = [BaseId, BaseId]);
        Should.Throw<InvalidDataException>(() => HeifReader.Read(file).GainMapOfPrimary(file)).Message.ShouldContain("both");
    }

    [Fact]
    public void ATmapAsAnInput_IsInvalidData()
    {
        var file = Build(Base256, Mono8, configure: o => o.Dimg = [BaseId, TmapId]);
        Should.Throw<InvalidDataException>(() => HeifReader.Read(file).GainMapOfPrimary(file)).Message.ShouldContain("'tmap'");
    }

    [Fact]
    public void AnInputThatIsNotAnImage_IsNotSupported()
    {
        // The Exif item stands in for an input of a type the reader does not decode.
        var builder = new HeifBuilder { PrimaryItemId = BaseId };
        AddImage(builder, BaseId, Base256, hidden: false, firstTileId: 10);
        builder.AddItem(new HeifBuilder.Item(MapId, "Exif", new byte[8], Hidden: true));
        builder.AddItem(new HeifBuilder.Item(TmapId, "tmap", TmapData(Scalar())));
        builder.Reference("dimg", TmapId, BaseId, MapId);
        var file = builder.Build();
        Should.Throw<NotSupportedException>(() => HeifReader.Read(file).GainMapOfPrimary(file)).Message.ShouldContain("'Exif'");
    }

    [Fact]
    public void BadMetadata_IsRefused()
    {
        byte[] Truncated() => TmapData(Scalar())[..20];
        foreach (var bytes in new[] { Truncated(), Array.Empty<byte>() })
        {
            var file = Build(Base256, Mono8, configure: o => o.TmapBytes = bytes);
            Should.Throw<InvalidDataException>(() => HeifReader.Read(file).GainMapOfPrimary(file));
        }

        // A minimum_version of 1 inside the ISO payload (the reader's own refusal), a zero denominator, and a tmap
        // version this reader does not know.
        var versioned = TmapData(Scalar());
        versioned[2] = 1;
        var zeroDenominator = TmapData(Scalar());
        zeroDenominator[6] = zeroDenominator[7] = zeroDenominator[8] = zeroDenominator[9] = 0;
        foreach (var bytes in new[] { versioned, zeroDenominator })
        {
            var file = Build(Base256, Mono8, configure: o => o.TmapBytes = bytes);
            Should.Throw<InvalidDataException>(() => HeifReader.Read(file).GainMapOfPrimary(file));
        }

        var unknownVersion = Build(Base256, Mono8, configure: o => o.TmapBytes = TmapData(Scalar(), version: 1));
        Should.Throw<NotSupportedException>(() => HeifReader.Read(unknownVersion).GainMapOfPrimary(unknownVersion)).Message.ShouldContain("version 1");
    }

    [Fact]
    public void OversizeMetadata_IsRefusedBeforeItIsCopied()
    {
        var file = Build(Base256, Mono8, configure: o => o.TmapBytes = [.. TmapData(Scalar()), .. new byte[5000]]);
        Should.Throw<InvalidDataException>(() => HeifReader.Read(file).GainMapOfPrimary(file)).Message.ShouldContain("bytes of metadata");
    }

    [Fact]
    public void AMetadataStructureFollowedByMoreBytes_IsAccepted()
    {
        // A newer writer may append fields a reader with the same minimum version skips.
        var file = Build(Base256, Mono8, configure: o => o.TmapBytes = [.. TmapData(Scalar()), 1, 2, 3, 4]);
        HeifReader.Read(file).GainMapOfPrimary(file).ShouldNotBeNull();
    }

    [Fact]
    public void TheBackwardDirection_IsNotSupported()
    {
        var backward = Scalar() with { BaseRenditionIsHdr = true };
        var file = Build(Base256, Mono8, backward);
        Should.Throw<NotSupportedException>(() => HeifGainMap.Decode(file)).Message.ShouldContain("backward");
    }

    [Fact]
    public void AMapLargerThanItsBase_IsInvalidData_BeforeAnythingIsDecoded()
    {
        var file = Build(Base256, Mono8 with { DeclaredSize = (400, 400) });
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file)).Message.ShouldContain("larger than its base");
    }

    [Fact]
    public void AMapOverTheImageLimit_IsInvalidData_BeforeAnythingIsDecoded()
    {
        // 16888 x 16888 is a legal HEVC picture size and 285 Mpixels, over HeifLimits.MaxImagePixels (2^28).
        var file = Build(Base256 with { DeclaredSize = (16888, 16888) }, Mono8 with { DeclaredSize = (16888, 16888) });
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file));
    }

    [Fact]
    public void ABaseOverTheReconstructionLimit_IsInvalidData_BeforeAnythingIsDecoded()
    {
        // 12000 x 8000 = 96 Mpixels: a legal image, over HeifLimits.MaxGainMapBasePixels (2^26 = 67 Mpixels).
        var file = Build(Base256 with { DeclaredSize = (12000, 8000) }, Mono8);
        Should.Throw<InvalidDataException>(() => HeifGainMap.Decode(file)).Message.ShouldContain("limit for a reconstruction");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonPositiveHeadroom_IsRefused(double headroom)
    {
        var file = Build(Base256, Mono8);
        Should.Throw<ArgumentOutOfRangeException>(() => HeifGainMap.Decode(file, displayHeadroom: headroom));
    }
}
