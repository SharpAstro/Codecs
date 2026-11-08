using System.Diagnostics;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 2's oracle: every HEVC picture in the Canon files decodes to exactly the samples ffmpeg's
/// decoder produces, plane by plane, with no tolerance. The files are fetched, not committed
/// (<c>fetch-heif-external.sh</c>), so these skip without them; ffmpeg goes through
/// <see cref="OracleGate"/>.
/// </summary>
public class HevcDecoderOracleTests(ITestOutputHelper output)
{
    [Fact]
    public void R7_Thumbnail_MatchesFfmpeg() => R7Items(heif => [heif.ThumbnailsOf(heif.PrimaryItem)[0]]); // 320x214

    [Fact]
    public void R7_Preview_MatchesFfmpeg() => R7Items(heif => [heif.ThumbnailsOf(heif.PrimaryItem)[1]]); // 1620x1080

    [Fact]
    public void R7_EveryGridTile_MatchesFfmpeg() => R7Items(heif =>
    {
        var grid = heif.DescribePrimary(HeifCanonFileTests.Load("canon-r7-hdrpq.hif")).Grid!;
        return grid.TileItemIds.Select(id => heif.GetItem(id)!).ToList();
    });

    [Theory]
    [InlineData("THMB")]
    [InlineData("PRVW")]
    public void R6_CrPreview_MatchesFfmpeg(string name)
    {
        var file = HeifCanonFileTests.Load("canon-r6-hdrpq.cr3");
        var preview = Cr3HevcPreviews.Find(file).Single(p => p.Name == name);
        Check(name, HeifProperties.ReadHevcConfiguration(preview.HvcC), preview.NalUnits);
    }

    private void R7Items(Func<HeifFile, IReadOnlyList<HeifItem>> pick)
    {
        var file = HeifCanonFileTests.Load("canon-r7-hdrpq.hif");
        var heif = HeifReader.Read(file);
        foreach (var item in pick(heif))
        {
            var image = heif.Describe(file, item);
            Check(item.ToString(), image.Coding, heif.ReadItemData(file, item));
        }
    }

    private void Check(string what, HevcDecoderConfiguration configuration, byte[] itemData)
    {
        var watch = Stopwatch.StartNew();
        var picture = HevcDecoder.Decode(configuration, itemData);
        var decodeTime = watch.Elapsed;
        var (luma, cb, cr) = HevcOracle.Decode(HevcOracle.AnnexB(configuration, itemData), picture);
        output.WriteLine($"{what}: {picture.Width}x{picture.Height}, decoded in {decodeTime.TotalMilliseconds:0} ms");

        var window = picture.ConformanceWindow;
        window.ShouldBe((0, 0, 0, 0), "the Canon pictures carry no conformance window; cropping is not compared here");
        Compare(what, "Y", picture.Luma, luma, picture.Width);
        Compare(what, "Cb", picture.Cb!, cb, picture.ChromaWidth);
        Compare(what, "Cr", picture.Cr!, cr, picture.ChromaWidth);
    }

    private static void Compare(string what, string plane, ushort[] ours, ushort[] theirs, int width)
    {
        ours.Length.ShouldBe(theirs.Length, $"{what} {plane} size");
        var differing = 0;
        var first = -1;
        for (var i = 0; i < ours.Length; i++)
        {
            if (ours[i] == theirs[i]) continue;
            if (first < 0) first = i;
            differing++;
        }
        if (differing == 0) return;
        Assert.Fail($"{what} {plane}: {differing} of {ours.Length} samples differ; the first at ({first % width}, {first / width}): " +
                    $"ours {ours[first]}, ffmpeg {theirs[first]}.");
    }
}
