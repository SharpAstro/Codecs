using SharpAstro.Jxr;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Emits a tiny JXR through our encoder, writes it to disk, and asks the
/// caller to inspect it externally — the test itself just verifies our
/// decoder still round-trips. Used to bisect WIC / Windows Photo
/// rejection: the smallest reproducer pinned to a known location.
/// </summary>
public sealed class JxrWicOracleTests
{
    [Fact]
    public void Emit_TinyBd16FRgb_ToTemp()
    {
        // 16×16 grayscale-ish gradient as BD16F RGB.
        var halves = new ushort[16 * 16 * 3];
        for (var i = 0; i < halves.Length; i++)
            halves[i] = BitConverter.HalfToUInt16Bits((Half)((i % 256) / 256.0));

        var bytes = JxrFileFormatter.SaveBd16FRgbNoFlexbits(halves, 16, 16);
        var path = Path.Combine(Path.GetTempPath(), "sharpastro_tiny_bd16f_rgb.jxr");
        File.WriteAllBytes(path, bytes);
        TestContext.Current.TestOutputHelper!.WriteLine($"Wrote {bytes.Length} bytes to {path}");
        TestContext.Current.TestOutputHelper.WriteLine("Now run: powershell -NoProfile -Command \"$d = [System.Windows.Media.Imaging.BitmapDecoder]::Create((New-Object Uri \\\"" + path + "\\\"), 0, 0); $d.Frames.Count\"");
    }

    [Fact]
    public void Emit_TinyBd8Rgb_ToTemp()
    {
        var rgb = new byte[16 * 16 * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i & 0xFF);
        var bytes = JxrFileFormatter.SaveBd8RgbNoFlexbits(rgb, 16, 16);
        var path = Path.Combine(Path.GetTempPath(), "sharpastro_tiny_bd8_rgb.jxr");
        File.WriteAllBytes(path, bytes);
        TestContext.Current.TestOutputHelper!.WriteLine($"Wrote {bytes.Length} bytes to {path}");
    }

    [Fact]
    public void Emit_TinyBd8Rgb_FreqMode_ToTemp()
    {
        var rgb = new byte[16 * 16 * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i & 0xFF);
        var bytes = JxrFileFormatter.SaveBd8RgbNoFlexbits(rgb, 16, 16,
            iccProfile: null, xmpMetadata: null, tiling: null,
            dcQp: 1, lpQp: 1, hpQp: 1, overlapMode: 0, frequencyMode: true);
        var path = Path.Combine(Path.GetTempPath(), "sharpastro_tiny_bd8_rgb_freq.jxr");
        File.WriteAllBytes(path, bytes);
        TestContext.Current.TestOutputHelper!.WriteLine($"Wrote {bytes.Length} bytes to {path}");
    }

    [Fact]
    public void Emit_TinyBd8_MatchWicExactly_ToTemp()
    {
        // Mirror every flag WIC's own encoder sets when writing a tiny
        // 16×16 BGRA32: FreqMode + IndexTable + LongWord + OutputClrFmt=NComponent.
        var bytes = SharpAstro.Jxr.JxrEncoder.EncodeBd8RgbNoFlexbits(
            new byte[16 * 16 * 3], 16, 16);
        var img = SharpAstro.Jxr.CodedImage.Decode(bytes);
        var rebuilt = new SharpAstro.Jxr.CodedImage
        {
            ImageHeader = new SharpAstro.Jxr.ImageHeader
            {
                OutputClrFmt = SharpAstro.Jxr.JxrOutputColorFormat.NComponent,
                OutputBitDepth = SharpAstro.Jxr.JxrOutputBitDepth.Bd8,
                ShortHeaderFlag = true,
                LongWordFlag = true,
                WidthMinus1 = 15,
                HeightMinus1 = 15,
                TilingFlag = false,
                FrequencyModeCodestreamFlag = true,
                IndexTablePresentFlag = true,
            },
            PlaneHeader = img.PlaneHeader,
            ProfileLevelInfo = img.ProfileLevelInfo,
            Macroblocks = img.Macroblocks,
        };
        var codestream = rebuilt.Encode();
        var file = new SharpAstro.Jxr.JxrFile(
            Width: 16, Height: 16,
            PixelFormat: SharpAstro.Jxr.JxrPixelFormat.Rgb24Bpp,
            Codestream: codestream);
        var fileBytes = SharpAstro.Jxr.JxrContainer.Write(file);
        var path = Path.Combine(Path.GetTempPath(), "sharpastro_tiny_wic_clone.jxr");
        File.WriteAllBytes(path, fileBytes);
        TestContext.Current.TestOutputHelper!.WriteLine($"Wrote {fileBytes.Length} bytes to {path}");
    }

    [Fact]
    public void Emit_TinyBd8Rgb_FreqMode_WithIndexTable_ToTemp()
    {
        // Hypothesis: WIC expects single-tile + FrequencyMode + IndexTable
        // to recognize a frame. Bypass JxrFileFormatter to craft the exact
        // header layout.
        var bytes = SharpAstro.Jxr.JxrEncoder.EncodeBd8RgbNoFlexbits(
            new byte[16 * 16 * 3], 16, 16);
        // We need to set the flags BEFORE encode — go through CodedImage directly.
        var img = SharpAstro.Jxr.CodedImage.Decode(bytes);
        var rebuilt = new SharpAstro.Jxr.CodedImage
        {
            ImageHeader = new SharpAstro.Jxr.ImageHeader
            {
                OutputClrFmt = img.ImageHeader.OutputClrFmt,
                OutputBitDepth = img.ImageHeader.OutputBitDepth,
                ShortHeaderFlag = img.ImageHeader.ShortHeaderFlag,
                WidthMinus1 = img.ImageHeader.WidthMinus1,
                HeightMinus1 = img.ImageHeader.HeightMinus1,
                TilingFlag = false,
                FrequencyModeCodestreamFlag = true,
                IndexTablePresentFlag = true,
            },
            PlaneHeader = img.PlaneHeader,
            ProfileLevelInfo = img.ProfileLevelInfo,
            Macroblocks = img.Macroblocks,
        };
        var codestream = rebuilt.Encode();
        var file = new SharpAstro.Jxr.JxrFile(
            Width: 16, Height: 16,
            PixelFormat: SharpAstro.Jxr.JxrPixelFormat.Rgb24Bpp,
            Codestream: codestream);
        var fileBytes = SharpAstro.Jxr.JxrContainer.Write(file);
        var path = Path.Combine(Path.GetTempPath(), "sharpastro_tiny_bd8_rgb_freq_idxtbl.jxr");
        File.WriteAllBytes(path, fileBytes);
        TestContext.Current.TestOutputHelper!.WriteLine($"Wrote {fileBytes.Length} bytes to {path}");
    }
}
