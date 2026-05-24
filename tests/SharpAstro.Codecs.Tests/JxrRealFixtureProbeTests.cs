using SharpAstro.Jxr;
using Shouldly;
using Xunit;
using Xunit.Sdk;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Probe tests that crack open the headers of real-world JXR fixtures —
/// produced by encoders other than ours — and report what's actually in
/// them. Useful both as documentation of which feature variants we'd need
/// to fully support these files, and as a cross-implementation sanity check
/// on our ImageHeader/ImagePlaneHeader parsers.
/// </summary>
public sealed class JxrRealFixtureProbeTests
{
    private readonly ITestOutputHelper _out;
    public JxrRealFixtureProbeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Probe_SeagullNebula_ImageHeader()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "seagull_nebula.jxr");
        var file = JxrContainer.Read(File.ReadAllBytes(path));
        var reader = new BitReader(file.Codestream);
        var img = ImageHeader.Read(ref reader);

        _out.WriteLine($"Seagull Nebula codestream (primary):");
        _out.WriteLine($"  Output format:        {img.OutputClrFmt} / {img.OutputBitDepth}");
        _out.WriteLine($"  Width × Height:       {img.WidthMinus1 + 1} × {img.HeightMinus1 + 1}");
        _out.WriteLine($"  ShortHeader:          {img.ShortHeaderFlag}");
        _out.WriteLine($"  Tiled:                {img.TilingFlag}");
        if (img.TilingFlag)
        {
            _out.WriteLine($"    NumVerTiles:        {img.NumVerTilesMinus1 + 1}");
            _out.WriteLine($"    NumHorTiles:        {img.NumHorTilesMinus1 + 1}");
            _out.WriteLine($"    Tile widths (MB):   [{string.Join(", ", img.TileWidthInMb)}]");
            _out.WriteLine($"    Tile heights (MB):  [{string.Join(", ", img.TileHeightInMb)}]");
        }
        _out.WriteLine($"  OverlapMode:          {img.OverlapMode}");
        _out.WriteLine($"  FrequencyMode:        {img.FrequencyModeCodestreamFlag}");
        _out.WriteLine($"  AlphaPlaneFlag:       {img.AlphaImagePlaneFlag}");
        _out.WriteLine($"  TrimFlexBits:         {img.TrimFlexBitsFlag}");
        _out.WriteLine($"  IndexTablePresent:    {img.IndexTablePresentFlag}");

        // Sanity: dimensions in IMAGE_HEADER match the container metadata.
        ((uint)(img.WidthMinus1 + 1)).ShouldBe(file.Width);
        ((uint)(img.HeightMinus1 + 1)).ShouldBe(file.Height);
    }

    [Fact]
    public void Probe_HdrFloat_ImageHeader()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hdr_128bpp_float_sample.jxr");
        var file = JxrContainer.Read(File.ReadAllBytes(path));
        var reader = new BitReader(file.Codestream);
        var img = ImageHeader.Read(ref reader);

        _out.WriteLine($"HDR Float sample codestream:");
        _out.WriteLine($"  Output format:        {img.OutputClrFmt} / {img.OutputBitDepth}");
        _out.WriteLine($"  Width × Height:       {img.WidthMinus1 + 1} × {img.HeightMinus1 + 1}");
        _out.WriteLine($"  ShortHeader:          {img.ShortHeaderFlag}");
        _out.WriteLine($"  Tiled:                {img.TilingFlag}");
        if (img.TilingFlag)
        {
            _out.WriteLine($"    NumVerTiles:        {img.NumVerTilesMinus1 + 1}");
            _out.WriteLine($"    NumHorTiles:        {img.NumHorTilesMinus1 + 1}");
            _out.WriteLine($"    Tile widths (MB):   [{string.Join(", ", img.TileWidthInMb)}]");
            _out.WriteLine($"    Tile heights (MB):  [{string.Join(", ", img.TileHeightInMb)}]");
        }
        _out.WriteLine($"  OverlapMode:          {img.OverlapMode}");
        _out.WriteLine($"  FrequencyMode:        {img.FrequencyModeCodestreamFlag}");
        _out.WriteLine($"  AlphaPlaneFlag:       {img.AlphaImagePlaneFlag}");
        _out.WriteLine($"  TrimFlexBits:         {img.TrimFlexBitsFlag}");
        _out.WriteLine($"  IndexTablePresent:    {img.IndexTablePresentFlag}");

        ((uint)(img.WidthMinus1 + 1)).ShouldBe(file.Width);
        ((uint)(img.HeightMinus1 + 1)).ShouldBe(file.Height);
    }
}
