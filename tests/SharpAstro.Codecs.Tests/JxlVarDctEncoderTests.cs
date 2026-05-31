using ImageMagick;
using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// VarDCT 5m — the capstone: our assembled lossy VarDCT .jxl is decoded by libjxl (via Magick), and
/// the reconstruction matches within RMSE (lossy). End-to-end proof that the frame framing
/// (ImageMetadata incl. the unconditional <c>default_m</c> bit / FrameHeader / TOC) + all four sections
/// (LfGlobal / LfGroup / HfGlobal / PassGroup) compose into a file the reference decoder accepts and
/// reconstructs correctly. (Independently confirmed against a from-source jxl-oxide build during
/// bring-up.) <see cref="OurDecoder_ParsesLibjxlVarDctOutput"/> checks the reverse direction.
/// </summary>
public sealed class JxlVarDctEncoderTests
{
    private static int[][] Solid(int w, int h, int r, int g, int b)
    {
        var ch = new int[3][];
        int[] v = [r, g, b];
        for (int c = 0; c < 3; c++) { ch[c] = new int[w * h]; Array.Fill(ch[c], v[c]); }
        return ch;
    }

    private static int[][] Gradient(int w, int h)
    {
        var ch = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            ch[c] = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    ch[c][y * w + x] = Math.Clamp(30 + c * 20 + x * 3 + y * 2, 0, 255);
        }
        return ch;
    }

    // A gradient that varies smoothly across the whole image (every 256-px group sees a different
    // sub-ramp), so a scrambled / mis-offset PassGroup would blow up the RMSE.
    private static int[][] FullGradient(int w, int h)
    {
        var ch = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            ch[c] = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    ch[c][y * w + x] = Math.Clamp(x * 200 / w + y * 40 / h + c * 15, 0, 255);
        }
        return ch;
    }

    private static double Rmse(int[][] rgb, MagickImage img, int w, int h)
    {
        using IPixelCollection<float> px = img.GetPixels();
        int mc = (int)px.Channels;
        float[] values = px.GetValues()!;
        double sumSq = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    double expected = rgb[c][y * w + x] / 255.0;
                    double actual = values[(y * w + x) * mc + c] / 65535.0;
                    double e = expected - actual;
                    sumSq += e * e;
                }
        return Math.Sqrt(sumSq / (3.0 * w * h));
    }

    [Fact]
    public void SelfRoundTrip_Gradient_ReconstructsWithinTolerance()
    {
        // Encoder -> our own VarDCT frame decoder: proves the assembled frame is internally
        // consistent (every section reader accepts what the writer emitted) before the libjxl check.
        const int w = 32, h = 32;
        int[][] rgb = Gradient(w, h);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        int[][] recon = JxlVarDctFrame.DecodeToRgb24(jxl);

        double sumSq = 0;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < w * h; i++)
            {
                double e = (rgb[c][i] - recon[c][i]) / 255.0;
                sumSq += e * e;
            }
        Math.Sqrt(sumSq / (3.0 * w * h)).ShouldBeLessThan(0.03);
    }

    [Theory]
    [InlineData(384, 320)] // 2x2 groups
    [InlineData(520, 264)] // 3x2 groups, partial last column
    public void MultiGroup_SelfRoundTrip_ReconstructsWithinTolerance(int w, int h)
    {
        int[][] rgb = FullGradient(w, h);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        int[][] recon = JxlVarDctFrame.DecodeToRgb24(jxl);

        double sumSq = 0;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < w * h; i++)
            {
                double e = (rgb[c][i] - recon[c][i]) / 255.0;
                sumSq += e * e;
            }
        Math.Sqrt(sumSq / (3.0 * w * h)).ShouldBeLessThan(0.05);
    }

    [Theory]
    [InlineData(264, 264)] // 2x2 groups (just over one 256-px group)
    [InlineData(512, 256)] // 2x1 groups
    [InlineData(384, 320)] // 2x2 groups
    [InlineData(520, 520)] // 3x3 groups (last group partial: 520 = 2*256 + 8)
    public void MultiGroup_DecodesInLibjxl_LowRmse(int w, int h)
    {
        int[][] rgb = FullGradient(w, h);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        using var img = new MagickImage(jxl); // libjxl decode — throws if malformed
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
        Rmse(rgb, img, w, h).ShouldBeLessThan(0.05);
    }

    [Theory]
    [InlineData(2304, 264)]  // 2 LF groups wide (just over 2048)
    [InlineData(4096, 512)]  // 2 LF groups wide, the literal "4096" case
    [InlineData(2304, 2176)] // 2x2 LF groups (both dims > 2048)
    public void MultiLfGroup_DecodesInLibjxl_LowRmse(int w, int h)
    {
        int[][] rgb = FullGradient(w, h);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        using var img = new MagickImage(jxl); // libjxl decode — throws if malformed
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
        Rmse(rgb, img, w, h).ShouldBeLessThan(0.05);
    }

    [Fact]
    public void MultiLfGroup_SelfRoundTrip_ReconstructsWithinTolerance()
    {
        const int w = 2304, h = 264; // 2 LF groups
        int[][] rgb = FullGradient(w, h);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        int[][] recon = JxlVarDctFrame.DecodeToRgb24(jxl);

        double sumSq = 0;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < w * h; i++)
            {
                double e = (rgb[c][i] - recon[c][i]) / 255.0;
                sumSq += e * e;
            }
        Math.Sqrt(sumSq / (3.0 * w * h)).ShouldBeLessThan(0.05);
    }

    // Past the old 4096 wall: a 4×4 LF-group grid (8192) and partial LF-group tiling. Validates that the
    // LF-group reassembly + per-group PassGroup offsets stay correct at scale, against libjxl itself.
    [Theory]
    [InlineData(8192, 512)]   // 4×1 LF groups wide
    [InlineData(512, 8192)]   // 1×4 LF groups tall
    [InlineData(4352, 4352)]  // 3×3 LF groups, partial last row/col (4352 = 2·2048 + 256)
    public void BigImage_DecodesInLibjxl_LowRmse(int w, int h)
    {
        int[][] rgb = FullGradient(w, h);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        using var img = new MagickImage(jxl); // libjxl decode — throws if malformed
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
        Rmse(rgb, img, w, h).ShouldBeLessThan(0.05);
    }

    [Fact]
    public void BigImage_SelfRoundTrip_8192Square()
    {
        // 4×4 LF groups, 32×32 = 1024 PassGroups (~67 MP). The heaviest self-consistency check.
        const int w = 8192, h = 8192;
        int[][] rgb = FullGradient(w, h);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        int[][] recon = JxlVarDctFrame.DecodeToRgb24(jxl);

        double sumSq = 0;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < w * h; i++)
            {
                double e = (rgb[c][i] - recon[c][i]) / 255.0;
                sumSq += e * e;
            }
        Math.Sqrt(sumSq / (3.0 * (double)w * h)).ShouldBeLessThan(0.05);
    }

    [Fact]
    public void Cap_RejectsOversizedDimensions()
    {
        int[][] rgb = Solid(8, 8, 0, 0, 0);
        Should.Throw<NotSupportedException>(() => JxlVarDctEncoder.EncodeRgb24(rgb, 16392, 8));
    }

    [Fact]
    public void OurDecoder_ParsesLibjxlVarDctOutput_EndToEnd()
    {
        // The complement of the libjxl-acceptance gap: our section readers must consume libjxl's own
        // lossy (VarDCT) output end-to-end without a parse/validation/EOF error — proving the readers
        // are bit-aligned against the reference encoder through LfGlobal (incl. custom HfBlockContext /
        // LfChannelCorrelation), GlobalModular, LfGroup, HfGlobal and the PassGroup. (Pixel-exact
        // reconstruction is a separate matter: libjxl applies Gabor/EPF/adaptive-LF-smoothing that our
        // minimal decoder skips, and uses its own quantizer settings.)
        const int w = 32, h = 32;
        using var img = new MagickImage(MagickColors.SteelBlue, w, h) { Quality = 90 };
        byte[] magick = img.ToByteArray(MagickFormat.Jxl);

        int[][] recon = JxlVarDctFrame.DecodeToRgb24(magick); // must not throw
        recon.Length.ShouldBe(3);
        recon[0].Length.ShouldBe(w * h);
    }

    [Fact]
    public void Solid_DecodesInLibjxl_LowRmse()
    {
        const int w = 32, h = 32;
        int[][] rgb = Solid(w, h, 180, 90, 60);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        using var img = new MagickImage(jxl); // libjxl decode — throws if our bytes are malformed
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
        Rmse(rgb, img, w, h).ShouldBeLessThan(0.03);
    }

    [Fact]
    public void Gradient_DecodesInLibjxl_LowRmse()
    {
        const int w = 64, h = 48;
        int[][] rgb = Gradient(w, h);
        byte[] jxl = JxlVarDctEncoder.EncodeRgb24(rgb, w, h);

        using var img = new MagickImage(jxl);
        img.Width.ShouldBe((uint)w);
        img.Height.ShouldBe((uint)h);
        Rmse(rgb, img, w, h).ShouldBeLessThan(0.05);
    }
}
