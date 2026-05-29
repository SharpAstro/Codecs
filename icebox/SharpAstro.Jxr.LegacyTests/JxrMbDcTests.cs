using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c tests for the DC-band MB orchestrator — T.832 §8.7.11
/// MB_DC + §8.7.12 DECODE_DC. Smaller-surface than HP/LP: just per-MB
/// super-DC per component.
/// </summary>
public sealed class JxrMbDcTests
{
    private static void RoundTrip(JxrInternalColorFormat fmt, int numComp, int[] dcValues)
    {
        var encState = new MbDcState();
        var w = new BitWriter();
        MbDc.EncodeMb(w, encState, fmt, numComp, dcValues);

        var decState = new MbDcState();
        var r = new BitReader(w.AsSpan());
        var decoded = new int[numComp];
        MbDc.DecodeMb(ref r, decState, fmt, numComp, decoded);

        for (var c = 0; c < numComp; c++)
            decoded[c].ShouldBe(dcValues[c], $"component {c}");

        // Encoder + decoder Model evolved identically.
        decState.Model.MBits0.ShouldBe(encState.Model.MBits0);
        decState.Model.MBits1.ShouldBe(encState.Model.MBits1);
    }

    [Fact]
    public void AllZeroDc_YOnly_RoundTrips()
    {
        RoundTrip(JxrInternalColorFormat.YOnly, 1, [0]);
    }

    [Fact]
    public void AllZeroDc_Rgb_RoundTrips()
    {
        RoundTrip(JxrInternalColorFormat.Rgb, 3, [0, 0, 0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(42)]
    [InlineData(-1000)]
    [InlineData(int.MaxValue / 1024)]
    public void SingleNonZeroDc_YOnly_RoundTrips(int dc)
    {
        RoundTrip(JxrInternalColorFormat.YOnly, 1, [dc]);
    }

    [Theory]
    [InlineData(JxrInternalColorFormat.Rgb)]
    [InlineData(JxrInternalColorFormat.YUV444)]
    [InlineData(JxrInternalColorFormat.YUV420)]
    [InlineData(JxrInternalColorFormat.YUV422)]
    public void YuvFormats_VariousDc_RoundTrip(JxrInternalColorFormat fmt)
    {
        // VAL_DC_YUV path. Try several combinations of zero/nonzero across Y/U/V.
        int[][] testCases =
        [
            [0, 0, 0],
            [100, 0, 0],
            [0, 50, 0],
            [0, 0, -50],
            [200, -100, 300],
            [-1000, 500, -200],
        ];
        foreach (var dc in testCases)
            RoundTrip(fmt, 3, dc);
    }

    [Theory]
    [InlineData(JxrInternalColorFormat.NComponent, 4)]
    [InlineData(JxrInternalColorFormat.YUVK, 4)]
    public void PerComponentFormats_VariousDc_RoundTrip(JxrInternalColorFormat fmt, int numComp)
    {
        // Per-component IS_DC_CH_FLAG path.
        var dc = new int[numComp];
        for (var i = 0; i < numComp; i++) dc[i] = (i + 1) * (i % 2 == 0 ? 1 : -1) * 100;
        RoundTrip(fmt, numComp, dc);
    }

    [Fact]
    public void RandomDcSweep_AllFormats()
    {
        (JxrInternalColorFormat fmt, int numComp)[] configs =
        [
            (JxrInternalColorFormat.YOnly, 1),
            (JxrInternalColorFormat.Rgb, 3),
            (JxrInternalColorFormat.YUV444, 3),
            (JxrInternalColorFormat.YUVK, 4),
            (JxrInternalColorFormat.NComponent, 4),
        ];
        var rng = new Random(0xDC4242);
        foreach (var (fmt, numComp) in configs)
        {
            for (var trial = 0; trial < 50; trial++)
            {
                var dc = new int[numComp];
                for (var i = 0; i < numComp; i++)
                    dc[i] = rng.Next(-10000, 10001);
                RoundTrip(fmt, numComp, dc);
            }
        }
    }

    [Fact]
    public void SequentialMbs_StateEvolvesIdentically()
    {
        // Run 5 MBs with random DC through one state, then decode through another.
        var rng = new Random(0xDC0DC0);
        var mbs = new int[5][];
        for (var i = 0; i < 5; i++)
        {
            mbs[i] = new int[3];
            for (var c = 0; c < 3; c++)
                mbs[i][c] = rng.Next(-5000, 5001);
        }

        var encState = new MbDcState();
        var w = new BitWriter();
        foreach (var dc in mbs)
            MbDc.EncodeMb(w, encState, JxrInternalColorFormat.Rgb, 3, dc);

        var decState = new MbDcState();
        var r = new BitReader(w.AsSpan());
        for (var i = 0; i < 5; i++)
        {
            var decoded = new int[3];
            MbDc.DecodeMb(ref r, decState, JxrInternalColorFormat.Rgb, 3, decoded);
            for (var c = 0; c < 3; c++)
                decoded[c].ShouldBe(mbs[i][c], $"MB {i} component {c}");
        }
    }
}
