using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Component tests for <see cref="ChromaUpsample"/> — the decode-side chroma
/// upsampler (jxrlib <c>interpolateUV</c>). Validated in isolation against the
/// bilinear interpolation rule before the full chroma decode slice exists:
/// a constant reduced field must stay constant after upsampling (catches
/// out-of-bounds / uninitialised reads and verifies full destination coverage,
/// incl. cross-MB indexing and the 420 next-MB-row lookahead), and a ramp must
/// produce the exact midpoint-averaged values with edge replication.
/// </summary>
public sealed class JxrChromaUpsampleTests
{
    private static readonly int[] Idx = SignalTransform.IdxCc;       // 16x16 luma layout
    private static readonly int[] Idx420 = ChromaUpsample.IdxCc420;  // 8x8 reduced-420 layout

    [Fact]
    public void Interpolate422_ConstantField_StaysConstantEverywhere()
    {
        const int mbCols = 2, mbRows = 2, k = 77;
        var srcU = Filled(mbCols * mbRows * 128, k);
        var srcV = Filled(mbCols * mbRows * 128, k);
        var dstU = Filled(mbCols * mbRows * 256, -999);
        var dstV = Filled(mbCols * mbRows * 256, -999);

        ChromaUpsample.Interpolate(ColorFormat.Yuv422, srcU, srcV, dstU, dstV, mbCols, mbRows);

        dstU.ShouldAllBe(v => v == k);
        dstV.ShouldAllBe(v => v == k);
    }

    [Fact]
    public void Interpolate420_ConstantField_StaysConstantEverywhere()
    {
        // 2x2 MB grid exercises cross-MB indexing AND the next-MB-row vertical lookahead
        // (MB row 0 peeks MB row 1) as well as the bottom-edge replicate (MB row 1).
        const int mbCols = 2, mbRows = 2, k = -33;
        var srcU = Filled(mbCols * mbRows * 64, k);
        var srcV = Filled(mbCols * mbRows * 64, k);
        var dstU = Filled(mbCols * mbRows * 256, 12345);
        var dstV = Filled(mbCols * mbRows * 256, 12345);

        ChromaUpsample.Interpolate(ColorFormat.Yuv420, srcU, srcV, dstU, dstV, mbCols, mbRows);

        dstU.ShouldAllBe(v => v == k);
        dstV.ShouldAllBe(v => v == k);
    }

    [Fact]
    public void Interpolate422_HorizontalRamp_AveragesOddColumns_ReplicatesRightEdge()
    {
        // Single MB. Reduced chroma column c (constant down rows) = c*16, c in 0..7.
        const int mbCols = 1, mbRows = 1;
        var srcU = new int[128];
        var srcV = new int[128];
        for (var row = 0; row < 16; row++)
            for (var c = 0; c < 8; c++)
            {
                int pos = Idx[row * 16 + c];
                srcU[pos] = c * 16;
                srcV[pos] = c * 16;
            }
        var dstU = new int[256];
        var dstV = new int[256];

        ChromaUpsample.Interpolate(ColorFormat.Yuv422, srcU, srcV, dstU, dstV, mbCols, mbRows);

        // Even col 2c = c*16; odd col = midpoint; col 15 replicates col 14. => col n = min(n,14)*8.
        for (var row = 0; row < 16; row++)
            for (var col = 0; col < 16; col++)
            {
                int expected = Math.Min(col, 14) * 8;
                dstU[Idx[row * 16 + col]].ShouldBe(expected);
                dstV[Idx[row * 16 + col]].ShouldBe(expected);
            }
    }

    [Fact]
    public void Interpolate420_VerticalRamp_AveragesOddRows_ReplicatesBottomEdge()
    {
        // Single MB (bottom edge). Reduced chroma row r (constant across cols) = r*16, r in 0..7.
        const int mbCols = 1, mbRows = 1;
        var srcU = new int[64];
        var srcV = new int[64];
        for (var r = 0; r < 8; r++)
            for (var c = 0; c < 8; c++)
            {
                int pos = Idx420[r * 8 + c];
                srcU[pos] = r * 16;
                srcV[pos] = r * 16;
            }
        var dstU = new int[256];
        var dstV = new int[256];

        ChromaUpsample.Interpolate(ColorFormat.Yuv420, srcU, srcV, dstU, dstV, mbCols, mbRows);

        // Even row 2r = r*16; odd row = midpoint; row 15 replicates row 14. => row n = min(n,14)*8,
        // constant across columns (the field is horizontally flat).
        for (var row = 0; row < 16; row++)
            for (var col = 0; col < 16; col++)
            {
                int expected = Math.Min(row, 14) * 8;
                dstU[Idx[row * 16 + col]].ShouldBe(expected);
                dstV[Idx[row * 16 + col]].ShouldBe(expected);
            }
    }

    private static int[] Filled(int length, int value)
    {
        var a = new int[length];
        Array.Fill(a, value);
        return a;
    }
}
