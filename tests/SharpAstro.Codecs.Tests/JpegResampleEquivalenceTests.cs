using SharpAstro.Jpeg;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// <see cref="JpegResample.Row"/> runs the <c>Vector128</c> form of V2/H2/Hv2 wherever the hardware has it, so the
/// golden digests only ever exercise one of the two on a given machine. These drive both
/// (<see cref="JpegResample.RowScalar"/>, the stb transliteration, and <see cref="JpegResample.RowSimd"/>) directly
/// and demand byte equality, over every width through two vector bodies and past (the body, the scalar tail and the
/// two edge samples each get hit), over rows that put the 16-bit lanes at their limits, and at non-zero row offsets
/// (where a load that ignored the offset would read the wrong row).
/// </summary>
public sealed class JpegResampleEquivalenceTests
{
    private static readonly int[] Kinds = [JpegResample.V2, JpegResample.H2, JpegResample.Hv2];

    private static string Name(int kind) => kind switch
    {
        JpegResample.V2 => "V2",
        JpegResample.H2 => "H2",
        _ => "Hv2",
    };

    private static void BothAgree(int kind, byte[] plane, int near, int far, int w, string because)
    {
        var scalar = new byte[2 * w + 8];
        var simd = new byte[2 * w + 8];
        // A canary past the row: a vector store that ran over the end would change it.
        Array.Fill(scalar, (byte)0xA5);
        Array.Fill(simd, (byte)0xA5);

        JpegResample.RowScalar(kind, scalar, plane, near, far, w, 2);
        JpegResample.RowSimd(kind, simd, plane, near, far, w, 2);

        simd.ShouldBe(scalar, $"{Name(kind)} w={w} near={near} far={far}: {because}");
    }

    private static byte[] Plane(int rowStride, Func<int, int, byte> sample, int rows = 2)
    {
        var plane = new byte[rowStride * rows];
        for (var r = 0; r < rows; r++)
            for (var x = 0; x < rowStride; x++)
                plane[r * rowStride + x] = sample(r, x);
        return plane;
    }

    [Fact]
    public void EveryWidth_OnRandomRows_IsByteEqual()
    {
        var rng = new Random(0x28);
        foreach (var kind in Kinds)
            for (var w = 1; w <= 100; w++)
            {
                var plane = Plane(w, (_, _) => (byte)rng.Next(256));
                BothAgree(kind, plane, 0, w, w, "random");
            }
    }

    [Fact]
    public void TheLanesAtTheirLimits_AreByteEqual()
    {
        // Hv2's t is 3*near + far, up to 1020, and 3*t + t' + 8 up to 4088: the largest the 16-bit lanes carry.
        // Alternating 0/255 makes every sum take both extremes across neighbouring samples.
        foreach (var kind in Kinds)
            foreach (var w in new[] { 17, 18, 19, 33, 34, 35, 50, 66, 67, 128, 129 })
            {
                BothAgree(kind, Plane(w, (r, x) => 255), 0, w, w, "all 255");
                BothAgree(kind, Plane(w, (r, x) => 0), 0, w, w, "all 0");
                BothAgree(kind, Plane(w, (r, x) => (byte)(((x + r) & 1) * 255)), 0, w, w, "alternating");
                BothAgree(kind, Plane(w, (r, x) => (byte)(r == 0 ? 255 : 0)), 0, w, w, "near 255, far 0");
                BothAgree(kind, Plane(w, (r, x) => (byte)(r == 0 ? 0 : 255)), 0, w, w, "near 0, far 255");
                BothAgree(kind, Plane(w, (r, x) => (byte)(x < w / 2 ? 0 : 255)), 0, w, w, "a step across the row");
            }
    }

    [Fact]
    public void ARowInsideALargerPlane_ReadsItsOwnRows()
    {
        // Rows at offsets inside a wider, taller plane, near below far and far below near, with the row length
        // shorter than the stride: loads must start at the row, not at the plane, and must not read the stride's tail.
        var rng = new Random(0x2828);
        foreach (var kind in Kinds)
            foreach (var w in new[] { 1, 2, 15, 16, 17, 18, 31, 32, 33, 34, 35, 64, 65, 100 })
            {
                const int rows = 5;
                var stride = w + 23;
                var plane = Plane(stride, (_, _) => (byte)rng.Next(256), rows);
                BothAgree(kind, plane, 1 * stride, 2 * stride, w, "near above far");
                BothAgree(kind, plane, 3 * stride, 2 * stride, w, "far above near");
                BothAgree(kind, plane, 2 * stride + 5, 4 * stride + 5, w, "a row that starts mid-stride");
            }
    }

    [Fact]
    public void ARowOfAnImageWidth_IsByteEqual()
    {
        // The widths a real decode asks for: a 5100-wide 4:2:0 chroma plane is 2550 samples, and odd ones.
        var rng = new Random(0x5100);
        foreach (var kind in Kinds)
            foreach (var w in new[] { 255, 256, 257, 1023, 1024, 1025, 2550, 3165 })
            {
                var plane = Plane(w, (_, _) => (byte)rng.Next(256));
                BothAgree(kind, plane, 0, w, w, "image width");
            }
    }

    [Fact]
    public void ADestinationTooShort_StillThrowsOnTheVectorPath()
    {
        // The vector bodies store unchecked, so the one check up front is what stands between a short buffer and
        // a write past it.
        foreach (var kind in Kinds)
        {
            const int w = 64;
            var plane = new byte[2 * w];
            var tooShort = new byte[kind == JpegResample.V2 ? w - 1 : 2 * w - 1];
            Should.Throw<ArgumentOutOfRangeException>(() =>
                JpegResample.RowSimd(kind, tooShort, plane, 0, w, w, 2), Name(kind));
        }
    }

    [Fact]
    public void ARowPastTheEndOfThePlane_StillThrowsOnTheVectorPath()
    {
        foreach (var kind in Kinds)
        {
            const int w = 64;
            var plane = new byte[w - 1];
            var dst = new byte[2 * w];
            Should.Throw<ArgumentOutOfRangeException>(() =>
                JpegResample.RowSimd(kind, dst, plane, 0, w, w, 2), Name(kind));
        }
    }

    [Fact]
    public void TheOtherKinds_GoThroughTheScalarKernel()
    {
        var rng = new Random(7);
        foreach (var kind in new[] { JpegResample.OneToOne, JpegResample.Generic })
            foreach (var w in new[] { 1, 17, 64 })
            {
                var plane = Plane(w, (_, _) => (byte)rng.Next(256));
                var a = new byte[4 * w];
                var b = new byte[4 * w];
                JpegResample.RowScalar(kind, a, plane, 0, w, w, 3);
                JpegResample.RowSimd(kind, b, plane, 0, w, w, 3);
                b.ShouldBe(a);
            }
    }
}
