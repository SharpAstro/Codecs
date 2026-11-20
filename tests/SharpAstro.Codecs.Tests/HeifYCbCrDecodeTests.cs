using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="HeifDecoder.DecodeYCbCr"/> hands out the planes <see cref="HeifDecoder.Decode"/> converts, so the planes
/// converted on the CPU (<see cref="HeifYCbCrImage.ToRgb"/>) must be <see cref="HeifDecoder.Decode"/>'s result
/// sample for sample: every chroma format, both ranges, a conformance window, and a grid whose last tiles are clipped.
/// </summary>
public sealed class HeifYCbCrDecodeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("x265-400p8")]
    [InlineData("x265-400p10")]
    [InlineData("x265-420p8-window")]
    [InlineData("x265-420p10-sao")]
    [InlineData("x265-420p12-loopfilters")]
    [InlineData("x265-422p10-modes")]
    [InlineData("x265-422p12")]
    public void Planes_ConvertedOnTheCpu_AreDecode(string fixture)
    {
        foreach (var fullRange in new[] { true, false })
        {
            // BT.2020 PQ as Canon writes it, at either range.
            var (file, width, height) = HeifDecodeTests.Wrap(fixture, HeifBuilder.Nclx(9, 16, 9, fullRange));
            var planes = HeifDecoder.DecodeYCbCr(file);
            (planes.Width, planes.Height).ShouldBe((width, height));
            planes.IsFullRange.ShouldBe(fullRange);
            planes.Luma.Length.ShouldBe(width * height);
            if (planes.ChromaFormatIdc == 0)
            {
                planes.Cb.ShouldBeNull();
            }
            else
            {
                planes.Cb.ShouldNotBeNull().Length.ShouldBe(planes.ChromaWidth * planes.ChromaHeight);
                planes.ChromaWidth.ShouldBe((width + (1 << planes.ChromaShiftX) - 1) >> planes.ChromaShiftX);
            }
            planes.ToRgb().Samples.ShouldBe(HeifDecoder.Decode(file).Samples, $"{fixture}, full range {fullRange}");
        }
    }

    [Fact]
    public void Grid_WithClippedTiles_ComposesThePlanesAsDecodeComposesRgb()
    {
        // 3 x 2 tiles of 230x166 into 680x320: the last column and row clipped, every tile with a conformance window.
        var tile = HevcFixtureTests.Load("x265-420p8-window").Item;
        var file = HeifParallelDecodeTests.Grid(680, 320, Enumerable.Repeat(tile, 6).ToArray());
        HeifDecoder.DecodeYCbCr(file).ToRgb().Samples.ShouldBe(HeifDecoder.Decode(file).Samples);
    }

    [Fact]
    public void Grid_422_ComposesThePlanes()
    {
        const string fixture = "x265-422p10-aq-scaling"; // 256x192
        var tile = HevcFixtureTests.Load(fixture).Item;
        var file = HeifParallelDecodeTests.Grid(500, 384, Enumerable.Repeat(tile, 4).ToArray(), columns: 2, rows: 2,
            fixture: fixture, tileWidth: 256, tileHeight: 192);
        var planes = HeifDecoder.DecodeYCbCr(file);
        (planes.ChromaShiftX, planes.ChromaShiftY, planes.ChromaWidth, planes.ChromaHeight).ShouldBe((1, 0, 250, 384));
        planes.ToRgb().Samples.ShouldBe(HeifDecoder.Decode(file).Samples);
    }

    [Fact]
    public void CodeToLinearTable_IsWhatToLinearRgbaFloatLooksUp()
    {
        var (file, _, _) = HeifDecodeTests.Wrap("x265-422p10-modes", HeifBuilder.Nclx(9, 16, 9, true));
        var planes = HeifDecoder.DecodeYCbCr(file);
        var table = planes.CodeToLinearTable();
        table.Length.ShouldBe(1024);
        var rgb = planes.ToRgb();
        var linear = rgb.ToLinearRgbaFloat(out _);
        for (var p = 0; p < rgb.Width * rgb.Height; p++)
            for (var c = 0; c < 3; c++)
                linear[4 * p + c].ShouldBe(table[rgb.Samples[3 * p + c]]);
    }

    [Fact]
    public void CodeToLinearTable_RefusesHlg()
    {
        var (file, _, _) = HeifDecodeTests.Wrap("x265-422p10-modes", HeifBuilder.Nclx(9, 18, 9, true));
        Should.Throw<NotSupportedException>(() => HeifDecoder.DecodeYCbCr(file).CodeToLinearTable()).Message.ShouldContain("HLG");
    }

    [Fact]
    public void R7_EveryImage_PlanesConvertToDecode()
    {
        var file = HeifCanonFileTests.Load("canon-r7-hdrpq.hif");
        var heif = HeifReader.Read(file);
        foreach (var item in heif.ThumbnailsOf(heif.PrimaryItem).Append(heif.PrimaryItem))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var planes = HeifDecoder.DecodeYCbCr(file, item);
            var planesMs = watch.ElapsedMilliseconds;
            watch.Restart();
            var rgb = HeifDecoder.Decode(file, item);
            output.WriteLine($"{item} {planes.Width}x{planes.Height}: planes {planesMs} ms, R'G'B' {watch.ElapsedMilliseconds} ms");
            planes.ChromaFormatIdc.ShouldBe(2);
            planes.ColorEncoding.ShouldBe(rgb.ColorEncoding);
            planes.ToRgb().Samples.ShouldBe(rgb.Samples, item.ToString());
        }
    }
}
