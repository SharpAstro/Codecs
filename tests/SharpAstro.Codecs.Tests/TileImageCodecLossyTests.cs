using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 7d (part 1) — lossy-QP validation of the OL_NONE codec. At QP index &gt; 0 the
/// quantizer is active (non-trivial man/exp reciprocal multiply, dequant ×Qp, and the
/// <c>iQP&lt;&lt;mbits</c> flexbits high-part path) — code the lossless tests never reach.
/// We can't check pixel identity, so we assert (a) the round-trip error is bounded (no
/// desync/garbage, no overflow) and (b) the codec is idempotent: re-encoding the decoded
/// image reproduces it exactly (a lossy codec must reach a fixed point).
/// </summary>
public sealed class TileImageCodecLossyTests
{
    private readonly ITestOutputHelper _out;
    public TileImageCodecLossyTests(ITestOutputHelper output) => _out = output;

    private static (int[] r, int[] g, int[] b) Gradient(int w, int h)
    {
        var r = new int[w * h];
        var g = new int[w * h];
        var b = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = (x * 3 + y * 2) & 0xff;
                g[i] = (128 + x - y) & 0xff;
                b[i] = (x + y * 3) & 0xff;
            }
        return (r, g, b);
    }

    private static (double rmse, int max) Error(int[] a, int[] b)
    {
        long sq = 0; int max = 0;
        for (var i = 0; i < a.Length; i++)
        {
            int d = a[i] - b[i];
            sq += (long)d * d;
            if (Math.Abs(d) > max) max = Math.Abs(d);
        }
        return (Math.Sqrt((double)sq / a.Length), max);
    }

    [Theory]
    [InlineData(5)]   // Qp = 2
    [InlineData(16)]  // Qp = 4
    [InlineData(32)]  // Qp = 8
    public void Gradient_Lossy_BoundedError_AndIdempotent(int qp)
    {
        const int w = 64, h = 48;
        var (r, g, b) = Gradient(w, h);

        // first pass
        var s1 = TileImageCodec.Encode(r, g, b, w, h, qp, qp, qp);
        var (r1, g1, b1) = (new int[w * h], new int[w * h], new int[w * h]);
        TileImageCodec.Decode(s1, w, h, r1, g1, b1, qp, qp, qp);

        var (er, mr) = Error(r, r1);
        var (eg, mg) = Error(g, g1);
        var (eb, mb) = Error(b, b1);
        _out.WriteLine($"QP idx {qp}: RMSE R={er:F2} G={eg:F2} B={eb:F2}; max R={mr} G={mg} B={mb}");

        // bounded — quantization error, not desync/garbage (observed RMSE ~1, max ~6 here)
        Math.Max(er, Math.Max(eg, eb)).ShouldBeLessThan(10.0, "RMSE bounded");
        Math.Max(mr, Math.Max(mg, mb)).ShouldBeLessThan(48, "max error bounded");

        // idempotence: re-encode the decoded image, decode again, expect a fixed point
        var s2 = TileImageCodec.Encode(r1, g1, b1, w, h, qp, qp, qp);
        var (r2, g2, b2) = (new int[w * h], new int[w * h], new int[w * h]);
        TileImageCodec.Decode(s2, w, h, r2, g2, b2, qp, qp, qp);

        for (var i = 0; i < w * h; i++)
        {
            r2[i].ShouldBe(r1[i], $"idempotent R[{i}] (qp {qp})");
            g2[i].ShouldBe(g1[i], $"idempotent G[{i}] (qp {qp})");
            b2[i].ShouldBe(b1[i], $"idempotent B[{i}] (qp {qp})");
        }
    }
}
