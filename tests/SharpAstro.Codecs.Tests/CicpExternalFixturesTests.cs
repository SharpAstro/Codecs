using System;
using System.IO;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Cross-check <see cref="PngReader"/> against the public PNG-3 cICP /
/// mDCV / cLLI conformance fixtures from Mike Pedersen's repository
/// (https://github.com/MikePedersen/CICP-Test-Files-PNG3rdEd-TIFF-MOV-MXF-AVIF-also-mDCV-and-cLLI).
/// A subset of the upstream PNG files is committed under
/// <c>Fixtures/Cicp/</c> -- see <c>ATTRIBUTION.md</c> there. Loaded from
/// the bin output via <see cref="AppContext.BaseDirectory"/> so the tests
/// run unconditionally on any machine that builds the project (no env-var
/// gate any more).
/// </summary>
public class CicpExternalFixturesTests
{
    private static readonly string FixturesRoot
        = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cicp");

    public static TheoryData<string, byte, byte, byte, bool> Cases = new()
    {
        // file name, primaries, transfer, matrix, fullRange
        { "PNG-SDR-BT.709-ColorBars-Tent-Valley-Grayscale-16bit-cICP-FR.png", 1, 1, 0, true },
        { "PNG-PQ-BT.2111-ColorBars-16bit-cICP-FR.png", 9, 16, 0, true },
        { "PNG-HLG-FancyColorBars-16bit-cICP-FR.png", 9, 18, 0, true },
        { "PNG-SDR-BT.709-ColorBars-Tent-Valley-Grayscale-16bit-cICP-NR.png", 1, 1, 0, false },
        { "PNG-HLG-FancyColorBars-16bit-cICP-NR.png", 9, 18, 0, false },
        { "SDR-Color-Bars-BT.709-16bit-1080-FR-BT.2020-12bitTest.png", 9, 15, 0, true },
        { "PNG-PQ-BT.2111-ColorBars-16bit-cICP-mDCV-max1Knit-min.0005nit-cLLI-FR.png", 9, 16, 0, true },
        { "PNG-PQ-BT.2111-ColorBars-16bit-cICP-mDCV-max4Knit-min.0005nit-cLLI-FR.png", 9, 16, 0, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReadsCicpFixture(string fileName, byte expPrimaries, byte expTransfer, byte expMatrix, bool expFullRange)
    {
        var path = Path.Combine(FixturesRoot, fileName);
        File.Exists(path).ShouldBeTrue($"fixture missing: {path}");

        var doc = PngReader.Decode(File.ReadAllBytes(path));
        doc.Cicp.ShouldNotBeNull($"{fileName} should have a cICP chunk");
        // Cast to byte for direct comparison with the H.273 codepoints in
        // the theory data -- the enum has names but the wire format is bytes.
        ((byte)doc.Cicp.ColorPrimaries).ShouldBe(expPrimaries);
        ((byte)doc.Cicp.TransferFunction).ShouldBe(expTransfer);
        ((byte)doc.Cicp.MatrixCoefficients).ShouldBe(expMatrix);
        doc.Cicp.VideoFullRangeFlag.ShouldBe(expFullRange);
    }

    [Fact]
    public void ReadsMdcvAndClli()
    {
        // 1000-nit fixture: cICP HDR10 + mDCV display-volume metadata +
        // cLLI content-light-level info (maxCLL=1000 nits, maxFALL=250 nits
        // per the filename). cLLI stores values in units of 0.0001 cd/m^2,
        // so the raw uint = nits * 10000. The upstream conformance fixture
        // uses the pre-final-spec "mDCv" chunk name; the reader accepts both
        // that and the final "mDCV" so we round-trip cleanly.
        var path = Path.Combine(FixturesRoot,
            "PNG-PQ-BT.2111-ColorBars-16bit-cICP-mDCV-max1Knit-min.0005nit-cLLI-FR.png");

        var doc = PngReader.Decode(File.ReadAllBytes(path));
        doc.Cicp.ShouldNotBeNull();
        doc.Mdcv.ShouldNotBeNull("mDCV chunk should be present and parsed");
        doc.Clli.ShouldNotBeNull("cLLI chunk should be present and parsed");
        doc.Clli.MaxCllUnits.ShouldBe(1000u * 10000u);
        doc.Clli.MaxFallUnits.ShouldBe(250u * 10000u);
    }
}
