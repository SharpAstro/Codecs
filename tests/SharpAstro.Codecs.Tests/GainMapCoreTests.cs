using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.GainMap;
using SharpAstro.Jpeg;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The format-independent gain-map core (<c>SharpAstro.GainMap</c>). The 8-bit sRGB path must stay
/// byte-identical to the loop that lived in <c>GainMapImage.ReconstructHdr</c> before the move, so this
/// file keeps a transcription of that loop as the reference. The linear-float path (what HEIF feeds)
/// is held to the same arithmetic, and per-channel parameters to a hand calculation.
/// </summary>
public sealed class GainMapCoreTests
{
    private static float[] Floats(RasterImage image) => MemoryMarshal.Cast<byte, float>(image.Pixels).ToArray();

    private static byte[] Noise(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    /// <summary>The pre-move <c>ReconstructHdr</c> loop, verbatim apart from its inputs.</summary>
    private static float[] OriginalLoop(byte[] basePixels, int width, int height, int baseChannels,
        byte[] map, int mapWidth, int mapHeight, int mapChannels, GainMapMetadata metadata, double displayHeadroom)
    {
        var logMinW = Math.Log2(metadata.HdrCapacityMin);
        var logMaxW = Math.Log2(metadata.HdrCapacityMax);
        var weight = Math.Clamp((Math.Log2(displayHeadroom) - logMinW) / (logMaxW - logMinW), 0, 1);
        var lut = SrgbTransfer.EotfLut8;
        var baseG = Math.Min(1, baseChannels - 1);
        var baseB = Math.Min(2, baseChannels - 1);
        var output = new float[width * height * 3];
        if (weight == 0)
        {
            for (int i = 0, o = 0; o < output.Length; i += baseChannels, o += 3)
            {
                output[o] = (float)lut[basePixels[i]];
                output[o + 1] = (float)lut[basePixels[i + baseG]];
                output[o + 2] = (float)lut[basePixels[i + baseB]];
            }
            return output;
        }

        var logMin = Math.Log2(metadata.GainMapMin);
        var logMax = Math.Log2(metadata.GainMapMax);
        var invGamma = 1.0 / metadata.Gamma;
        var usableMapChannels = mapChannels >= 3 ? 3 : 1;
        for (var y = 0; y < height; y++)
        {
            var v = (y + 0.5) * mapHeight / height - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(v), 0, mapHeight - 1);
            var y1 = Math.Min(y0 + 1, mapHeight - 1);
            var fy = Math.Clamp(v - y0, 0, 1);
            for (var x = 0; x < width; x++)
            {
                var u = (x + 0.5) * mapWidth / width - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(u), 0, mapWidth - 1);
                var x1 = Math.Min(x0 + 1, mapWidth - 1);
                var fx = Math.Clamp(u - x0, 0, 1);
                var row0 = y0 * mapWidth;
                var row1 = y1 * mapWidth;
                var baseIndex = (y * width + x) * baseChannels;
                var outIndex = (y * width + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var mc = c < usableMapChannels ? c : 0;
                    var code = (map[(row0 + x0) * mapChannels + mc] * (1 - fx) + map[(row0 + x1) * mapChannels + mc] * fx) * (1 - fy)
                             + (map[(row1 + x0) * mapChannels + mc] * (1 - fx) + map[(row1 + x1) * mapChannels + mc] * fx) * fy;
                    var recovery = code / 255.0;
                    if (invGamma != 1.0)
                        recovery = Math.Pow(recovery, invGamma);
                    var gain = Math.Pow(2, (logMin + (logMax - logMin) * recovery) * weight);
                    var sdrLinear = lut[basePixels[baseIndex + Math.Min(c, baseChannels - 1)]];
                    output[outIndex + c] = (float)((sdrLinear + metadata.OffsetSdr) * gain - metadata.OffsetHdr);
                }
            }
        }
        return output;
    }

    public static TheoryData<int, int, int, int, int, int, double, double> Shapes => new()
    {
        // width, height, baseChannels, mapWidth, mapHeight, mapChannels, gamma, headroom
        { 17, 13, 3, 5, 4, 1, 1.0, 4.0 },
        { 17, 13, 3, 5, 4, 3, 2.2, 2.0 },
        { 16, 16, 4, 4, 4, 1, 0.8, 3.0 },
        { 9, 7, 1, 3, 2, 3, 1.0, 1.0 },
        { 20, 20, 3, 20, 20, 3, 1.0, 100.0 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Srgb8Path_IsByteIdenticalToTheOriginalLoop(int w, int h, int bc, int mw, int mh, int mc, double gamma, double headroom)
    {
        var meta = new GainMapMetadata { GainMapMin = 0.5, GainMapMax = 6.0, Gamma = gamma, HdrCapacityMax = 4.0, OffsetSdr = 0.02, OffsetHdr = 0.03 };
        var basePixels = Noise(w * h * bc, 1);
        var map = Noise(mw * mh * mc, 2);

        var actual = Floats(GainMapReconstructor.ReconstructFromSrgb8(basePixels, w, h, bc, map, mw, mh, mc, meta, headroom));
        var expected = OriginalLoop(basePixels, w, h, bc, map, mw, mh, mc, meta, headroom);

        actual.Length.ShouldBe(expected.Length);
        for (var i = 0; i < expected.Length; i++)
            BitConverter.SingleToInt32Bits(actual[i]).ShouldBe(BitConverter.SingleToInt32Bits(expected[i]), $"sample {i}");
    }

    [Fact]
    public void LinearPath_MatchesTheSrgb8Path_WhenGivenTheSameLightAndNormalisedCodes()
    {
        const int w = 12, h = 9, mw = 3, mh = 3;
        var meta = new GainMapMetadata { GainMapMin = 1.0, GainMapMax = 8.0, Gamma = 1.5, HdrCapacityMax = 8.0 };
        var basePixels = Noise(w * h * 3, 3);
        var map = Noise(mw * mh, 4);
        var baseLinear = basePixels.Select(b => (float)SrgbTransfer.EotfLut8[b]).ToArray();
        var mapNormalised = map.Select(b => b / 255f).ToArray();

        var viaSrgb = Floats(GainMapReconstructor.ReconstructFromSrgb8(basePixels, w, h, 3, map, mw, mh, 1, meta, 8.0));
        var viaLinear = Floats(GainMapReconstructor.ReconstructFromLinear(baseLinear, w, h, 3, mapNormalised, mw, mh, 1, meta, 8.0));

        for (var i = 0; i < viaSrgb.Length; i++)
            viaLinear[i].ShouldBe(viaSrgb[i], 1e-5f);
    }

    [Fact]
    public void LinearPath_TakesABaseOfAnyBitDepth_AsFloats()
    {
        // A 10-bit-derived base: values that are not 8-bit sRGB codes at all.
        float[] baseLinear = [0.0f, 0.1f, 0.25f, 0.5f, 0.75f, 1.0f];
        float[] map = [1f, 1f];
        var meta = new GainMapMetadata { GainMapMin = 1.0, GainMapMax = 4.0, HdrCapacityMax = 4.0, OffsetSdr = 0.0, OffsetHdr = 0.0 };

        var full = Floats(GainMapReconstructor.ReconstructFromLinear(baseLinear, 2, 1, 3, map, 1, 1, 1, meta, 4.0));
        full.ShouldBe([0f, 0.4f, 1f, 2f, 3f, 4f], 1e-6f);

        var sdr = Floats(GainMapReconstructor.ReconstructFromLinear(baseLinear, 2, 1, 3, map, 1, 1, 1, meta, 1.0));
        sdr.ShouldBe(baseLinear);
    }

    [Fact]
    public void PerChannelParameters_AreAppliedToTheirOwnChannel()
    {
        var meta = new GainMapMetadata
        {
            GainMapMax = 2.0,
            HdrCapacityMax = 8.0,
            OffsetSdr = 0.0,
            OffsetHdr = 0.0,
            Channels =
            [
                new GainMapChannel(1.0, 2.0, 1.0, 0.0, 0.0),
                new GainMapChannel(1.0, 4.0, 1.0, 0.0, 0.0),
                new GainMapChannel(1.0, 8.0, 1.0, 0.0, 0.0),
            ],
        };
        float[] baseLinear = [0.5f, 0.5f, 0.5f];
        float[] map = [1f]; // full recovery, one shared channel

        var hdr = Floats(GainMapReconstructor.ReconstructFromLinear(baseLinear, 1, 1, 3, map, 1, 1, 1, meta, 8.0));

        hdr.ShouldBe([1f, 2f, 4f], 1e-6f);
        meta.ChannelCount.ShouldBe(3);
        meta.GetChannel(2).GainMapMax.ShouldBe(8.0);
    }

    [Fact]
    public void ScalarMetadata_IsTheOneChannelCase()
    {
        var meta = new GainMapMetadata { GainMapMin = 0.5, GainMapMax = 3.0, Gamma = 1.2, OffsetSdr = 0.01, OffsetHdr = 0.02, HdrCapacityMax = 3.0 };
        meta.ChannelCount.ShouldBe(1);
        for (var c = 0; c < 3; c++)
            meta.GetChannel(c).ShouldBe(new GainMapChannel(0.5, 3.0, 1.2, 0.01, 0.02));
    }

    [Fact]
    public void HeadroomsAndColourSpaceFlag_AreRecordedNotApplied()
    {
        var meta = new GainMapMetadata { GainMapMax = 4.0, HdrCapacityMin = 1.0, HdrCapacityMax = 4.0, UseBaseColourSpace = false };
        meta.BaselineHdrHeadroom.ShouldBe(1.0);
        meta.AlternateHdrHeadroom.ShouldBe(4.0);
        meta.UseBaseColourSpace.ShouldBeFalse();
        new GainMapMetadata { GainMapMax = 4.0, HdrCapacityMax = 4.0 }.UseBaseColourSpace.ShouldBeTrue();

        // The flag changes nothing in the math: same pixels either way.
        float[] baseLinear = [0.2f, 0.4f, 0.6f];
        float[] map = [0.5f];
        var a = Floats(GainMapReconstructor.ReconstructFromLinear(baseLinear, 1, 1, 3, map, 1, 1, 1, meta, 4.0));
        var b = Floats(GainMapReconstructor.ReconstructFromLinear(baseLinear, 1, 1, 3, map, 1, 1, 1, meta with { UseBaseColourSpace = true }, 4.0));
        a.ShouldBe(b);
    }

    [Fact]
    public void BadChannelCount_IsRefused()
    {
        var meta = new GainMapMetadata { GainMapMax = 4.0, HdrCapacityMax = 4.0, Channels = [new GainMapChannel(1, 4, 1, 0, 0), new GainMapChannel(1, 4, 1, 0, 0)] };
        Should.Throw<InvalidOperationException>(meta.Validate);
    }

    [Fact]
    public void ShortBuffers_AreRefused()
    {
        var meta = new GainMapMetadata { GainMapMax = 4.0, HdrCapacityMax = 4.0 };
        Should.Throw<ArgumentException>(() => GainMapReconstructor.ReconstructFromLinear(new float[5], 2, 1, 3, new float[1], 1, 1, 1, meta, 4.0));
        Should.Throw<ArgumentException>(() => GainMapReconstructor.ReconstructFromLinear(new float[6], 2, 1, 3, new float[1], 2, 1, 1, meta, 4.0));
    }
}
