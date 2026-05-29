using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c tests for the LP-band MB orchestrator — T.832 §8.7.16
/// MB_LP + §8.7.16.2 REFINE_LP. Covers the CBPLP_CH_BIT path
/// (YOnly / Rgb / NComponent / YUVK) end-to-end including REFINE_LP
/// refinement bits.
/// </summary>
public sealed class JxrMbLpTests
{
    private static void RoundTrip(JxrInternalColorFormat fmt, int numComp, int[] lp)
    {
        var encState = new MbLpState();
        var w = new BitWriter();
        MbLp.EncodeMb(w, encState, fmt, numComp, lp);

        var decState = new MbLpState();
        var r = new BitReader(w.AsSpan());
        var decoded = new int[numComp * 16];
        MbLp.DecodeMb(ref r, decState, fmt, numComp, decoded);

        for (var c = 0; c < numComp; c++)
            for (var p = 1; p < 16; p++)
                decoded[c * 16 + p].ShouldBe(lp[c * 16 + p], $"component {c} position {p}");
    }

    [Fact]
    public void AllZeroLp_YOnly_RoundTrips()
    {
        RoundTrip(JxrInternalColorFormat.YOnly, 1, new int[16]);
    }

    [Fact]
    public void AllZeroLp_Rgb_RoundTrips()
    {
        RoundTrip(JxrInternalColorFormat.Rgb, 3, new int[48]);
    }

    [Fact]
    public void SmallLpValues_RoundTrip()
    {
        // Values fitting entirely in the iModelBits FLC refinement
        // (initial state has iModelBits = 4 for LP, so absLp < 16 fits in low bits).
        var lp = new int[16];
        for (var p = 1; p < 16; p++) lp[p] = ((p & 1) == 0 ? p : -p);
        RoundTrip(JxrInternalColorFormat.YOnly, 1, lp);
    }

    [Fact]
    public void LargeLpValues_RoundTrip()
    {
        // Values requiring block-coded high bits beyond iModelBits.
        var lp = new int[16];
        for (var p = 1; p < 16; p++) lp[p] = (p * 100) * (p % 2 == 0 ? 1 : -1);
        RoundTrip(JxrInternalColorFormat.YOnly, 1, lp);
    }

    [Theory]
    [InlineData(JxrInternalColorFormat.YOnly, 1)]
    [InlineData(JxrInternalColorFormat.Rgb, 3)]
    [InlineData(JxrInternalColorFormat.NComponent, 4)]
    [InlineData(JxrInternalColorFormat.YUVK, 4)]
    public void RandomLp_AllSupportedFormats(JxrInternalColorFormat fmt, int numComp)
    {
        var rng = new Random(0xDEC0DE + (int)fmt);
        for (var trial = 0; trial < 30; trial++)
        {
            var lp = new int[numComp * 16];
            for (var c = 0; c < numComp; c++)
                for (var p = 1; p < 16; p++)
                    lp[c * 16 + p] = rng.Next(-200, 201);
            RoundTrip(fmt, numComp, lp);
        }
    }

    [Fact]
    public void YUV420And422_NotYetSupported_Throws()
    {
        // Phase 22 wired YUV444's CBPLP_YUV1/YUV2 joint VLC path. YUV420/422
        // still throw because they additionally need chroma-subsampled block
        // counts in the HP layer + Table 56 (2-bit CBPLP_YUV1) instead of
        // Table 55. YUV444 is sufficient for the WIC RGB-interop use case.
        var state = new MbLpState();
        var w = new BitWriter();
        foreach (var fmt in new[] { JxrInternalColorFormat.YUV420, JxrInternalColorFormat.YUV422 })
        {
            Should.Throw<NotSupportedException>(() =>
                MbLp.EncodeMb(w, state, fmt, 3, new int[48]));
        }
    }

    [Fact]
    public void YUV444_RoundTrips_ViaJointCbplpVlc()
    {
        // YUV444 MB_LP encode/decode through the CBPLP_YUV1/2 dispatch.
        var enc = new MbLpState();
        var dec = new MbLpState();
        var rng = new Random(unchecked((int)0x59C0C603));
        var w = new BitWriter();
        // Vary content to exercise both the YUV2 fixed-width and YUV1 VLC paths
        // (the latter kicks in only after CountZero/Max drift past their
        // thresholds, which doesn't happen at MB 0 — so this primarily tests
        // YUV2). Multiple MBs would walk into YUV1 but the state-sync between
        // encoder and decoder is the critical invariant — verify it.
        for (var mb = 0; mb < 8; mb++)
        {
            var lp = new int[48];
            for (var i = 0; i < lp.Length; i++) lp[i] = i % 16 == 0 ? 0 : rng.Next(-8, 8);
            MbLp.EncodeMb(w, enc, JxrInternalColorFormat.YUV444, 3, lp);
        }

        var bytes = w.ToArray();
        var r = new BitReader(bytes);
        var decRng = new Random(unchecked((int)0x59C0C603));
        for (var mb = 0; mb < 8; mb++)
        {
            var srcLp = new int[48];
            for (var i = 0; i < srcLp.Length; i++) srcLp[i] = i % 16 == 0 ? 0 : decRng.Next(-8, 8);
            var got = new int[48];
            MbLp.DecodeMb(ref r, dec, JxrInternalColorFormat.YUV444, 3, got);
            for (var i = 0; i < 48; i++)
                if (i % 16 != 0) // position 0 is super-DC, untouched by MbLp
                    got[i].ShouldBe(srcLp[i], $"mb {mb} pos {i}");
        }
    }

    [Fact]
    public void SequentialMbs_StateEvolvesIdentically()
    {
        // Verify state sharing across MBs — broken AbsLevel sharing would
        // desynchronize encoder/decoder by the 2nd MB.
        var rng = new Random(0x5678);
        var mbs = new int[4][];
        for (var i = 0; i < 4; i++)
        {
            mbs[i] = new int[48];
            for (var c = 0; c < 3; c++)
                for (var p = 1; p < 16; p++)
                    mbs[i][c * 16 + p] = rng.Next(-150, 151);
        }

        var encState = new MbLpState();
        var w = new BitWriter();
        foreach (var lp in mbs)
            MbLp.EncodeMb(w, encState, JxrInternalColorFormat.Rgb, 3, lp);

        var decState = new MbLpState();
        var r = new BitReader(w.AsSpan());
        for (var i = 0; i < 4; i++)
        {
            var decoded = new int[48];
            MbLp.DecodeMb(ref r, decState, JxrInternalColorFormat.Rgb, 3, decoded);
            for (var c = 0; c < 3; c++)
                for (var p = 1; p < 16; p++)
                    decoded[c * 16 + p].ShouldBe(mbs[i][c * 16 + p], $"MB {i} c={c} p={p}");
        }
    }
}
