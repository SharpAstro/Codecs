using SharpAstro.Jpeg;
using Shouldly;
using Kind = SharpAstro.Codecs.Tests.JpegRegionDecodeTests.Kind;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <c>JpegDecoder.DecodeGrayTo</c> decodes at one byte a pixel without making the RGBA: each row is converted in
/// a row of scratch and reduced at once. Its contract is BT.601 luma, <c>(77 R + 150 G + 29 B + 128) &gt;&gt; 8</c>,
/// of what <c>DecodeTo</c> gives, so the expected side here is always <c>DecodeTo</c> reduced by that formula, and for
/// a grey stream it is <c>DecodeTo</c>'s R channel itself: the weights sum to 256, so a sample is its own luma.
/// Nothing is "within 1".
/// </summary>
public sealed class JpegGrayDecodeTests
{
    private static readonly JpegScale[] Scales = [JpegScale.Full, JpegScale.Half, JpegScale.Quarter, JpegScale.Eighth];

    private const byte Sentinel = 0xAB;

    public static IEnumerable<object[]> Matrix() =>
        from layout in JpegRegionDecodeTests.Layouts
        from kind in Enum.GetValues<Kind>()
        where kind != Kind.WholePlanes   // a test-only engine switch DecodeGrayTo does not expose
        select new object[] { layout ?? "grey", kind };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void GrayIsTheLumaOfTheRgbaDecode(string layoutName, object kindObject)
    {
        var kind = (Kind)kindObject;
        var layout = JpegRegionDecodeTests.LayoutOf(layoutName);
        // An odd size, so the last MCU column and row are partial at every scale.
        var jpeg = JpegRegionDecodeTests.Build(77, 53, layout, kind);
        foreach (var scale in Scales)
        {
            var (w, h) = JpegDecoder.ReadInfo(jpeg).ScaledSize(scale);
            var rgba = new byte[w * h * 4];
            JpegDecoder.DecodeTo(jpeg, rgba, scale).ShouldBe((w, h));

            var gray = new byte[w * h + 16];
            gray.AsSpan().Fill(Sentinel);
            JpegDecoder.DecodeGrayTo(jpeg, gray, scale).ShouldBe((w, h));

            for (var i = 0; i < w * h; i++)
            {
                var r = rgba[i * 4];
                var expected = layout is null
                    ? r
                    : (byte)((77 * r + 150 * rgba[i * 4 + 1] + 29 * rgba[i * 4 + 2] + 128) >> 8);
                if (gray[i] != expected)
                    throw new ShouldAssertException($"{layout ?? "grey"} {kind} {scale}: pixel {i % w},{i / w} is {gray[i]}, the RGBA decode says {expected}");
            }
            gray.AsSpan(w * h).ToArray().ShouldAllBe(b => b == Sentinel, "nothing is written past width times height");
        }
    }

    [Fact]
    public void AGreyStreamIsItsSampleExactly()
    {
        var jpeg = JpegRegionDecodeTests.Build(64, 48, null, Kind.RestartRow);
        var (w, h) = JpegDecoder.ReadInfo(jpeg).ScaledSize(JpegScale.Full);
        var rgba = new byte[w * h * 4];
        JpegDecoder.DecodeTo(jpeg, rgba);
        var gray = new byte[w * h];
        JpegDecoder.DecodeGrayTo(jpeg, gray);

        for (var i = 0; i < w * h; i++)
        {
            rgba[i * 4].ShouldBe(rgba[i * 4 + 1]);
            rgba[i * 4].ShouldBe(rgba[i * 4 + 2]);
            gray[i].ShouldBe(rgba[i * 4]);
        }
    }

    [Fact]
    public void ADestinationTooSmallIsRefusedBeforeAnythingIsWritten()
    {
        var jpeg = JpegRegionDecodeTests.Build(40, 30, "2x2,1x1,1x1", Kind.Baseline);
        var gray = new byte[40 * 30 - 1];
        gray.AsSpan().Fill(Sentinel);

        Should.Throw<ArgumentException>(() => JpegDecoder.DecodeGrayTo(jpeg, gray));
        gray.ShouldAllBe(b => b == Sentinel);
    }
}
