using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase 21b: round-trip tests for the iModelBits coefficient split + inline
/// FlexBits emission in <see cref="MbHp"/>. Verifies the SPATIAL-mode layout
/// where MB_HP_FLEX bits follow the HP VLC pass in the same stream.
/// </summary>
public sealed class JxrMbHpFlexBitsTests
{
    /// <summary>Build a 16-block AC buffer with deterministic coefficients.</summary>
    private static int[] BuildMb(int seed)
    {
        var rng = new Random(seed);
        var mb = new int[256];
        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
                if (rng.Next(0, 3) == 0)
                    mb[b * 16 + p] = rng.Next(-200, 201);
        return mb;
    }

    /// <summary>
    /// With iModelBits = 0, the new overload's bitstream output must match
    /// the legacy overload bit-for-bit (split is a no-op, no FlexBits emitted).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void IModelBitsZero_MatchesLegacyBitstream(int mbhpMode)
    {
        var mb = BuildMb(seed: 0x21B0);

        var legacyState = new MbHpState();
        var legacyW = new BitWriter();
        var legacyCbphp = MbHp.EncodeLumaMb(legacyW, legacyState, mbhpMode, mb);

        var newState = new MbHpState();
        var newW = new BitWriter();
        var newCbphp = MbHp.EncodeLumaMb(newW, newState, mbhpMode, trimFlexBits: 0, mb);

        newCbphp.ShouldBe(legacyCbphp);
        newW.BitPosition.ShouldBe(legacyW.BitPosition,
            "with iModelBits=0 the new overload must emit identical bits to the legacy path");
        newW.ToArray().ShouldBe(legacyW.ToArray());
    }

    /// <summary>
    /// Lossless round-trip with iModelBits > 0, trimFlexBits = 0. The split
    /// puts low bits into FlexBits; reconstruction must be exact.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 3)]
    [InlineData(0, 5)]
    [InlineData(1, 4)]
    public void Lossless_RoundTrip_WithIModelBits(int mbhpMode, int iModelBits)
    {
        var mb = BuildMb(seed: 0x21B1 ^ iModelBits);

        var encState = new MbHpState();
        encState.Model.MBits0 = iModelBits;
        var w = new BitWriter();
        var cbphp = MbHp.EncodeLumaMb(w, encState, mbhpMode, trimFlexBits: 0, mb);

        var decState = new MbHpState();
        decState.Model.MBits0 = iModelBits;
        var r = new BitReader(w.AsSpan());
        var decoded = new int[256];
        MbHp.DecodeLumaMb(ref r, decState, mbhpMode, trimFlexBits: 0, cbphp, decoded);

        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
                decoded[b * 16 + p].ShouldBe(mb[b * 16 + p], $"block {b} pos {p}");
    }

    /// <summary>
    /// Lossy round-trip — with trimFlexBits = 2, the bottom 2 bits of every
    /// AC coefficient magnitude are dropped. Reconstruction recovers values
    /// equal to <c>sign(x) * (|x| &amp; ~3)</c>.
    /// </summary>
    [Fact]
    public void Lossy_TrimFlexBits_DropsLowBits()
    {
        const int iModelBits = 4;
        const int trimFlexBits = 2;
        var mb = BuildMb(seed: 0x21B2);

        var encState = new MbHpState();
        encState.Model.MBits0 = iModelBits;
        var w = new BitWriter();
        var cbphp = MbHp.EncodeLumaMb(w, encState, mbhpMode: 0, trimFlexBits, mb);

        var decState = new MbHpState();
        decState.Model.MBits0 = iModelBits;
        var r = new BitReader(w.AsSpan());
        var decoded = new int[256];
        MbHp.DecodeLumaMb(ref r, decState, mbhpMode: 0, trimFlexBits, cbphp, decoded);

        const int mask = ~((1 << trimFlexBits) - 1); // ~0b11 = ...11111100
        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
            {
                var orig = mb[b * 16 + p];
                var absExpected = (orig < 0 ? -orig : orig) & mask;
                var expected = orig < 0 ? -absExpected : absExpected;
                decoded[b * 16 + p].ShouldBe(expected, $"block {b} pos {p}: orig={orig}");
            }
    }

    /// <summary>
    /// Coefficients smaller than <c>1 &lt;&lt; iModelBits</c> have VLC = 0 but
    /// non-zero FlexBits refinements. Verifies the SIGN_FLAG path in
    /// BlockFlexBits is exercised correctly through MbHp.
    /// </summary>
    [Fact]
    public void SmallCoefficients_VlcZero_FlexCarriesSign()
    {
        const int iModelBits = 5; // values < 32 have vlc=0
        var mb = new int[256];
        // Sprinkle small +/-/zero values across a couple of blocks. All have
        // |v| < 32, so the VLC pass produces vlc=0 for every position and the
        // CBPHP should be 0 everywhere — but FlexBits still encodes signs/refs.
        mb[0 * 16 + 1] = 7;
        mb[0 * 16 + 4] = -15;
        mb[5 * 16 + 3] = 1;
        mb[5 * 16 + 14] = -31;
        mb[10 * 16 + 9] = 0; // explicit zero
        mb[10 * 16 + 10] = 25;

        var encState = new MbHpState();
        encState.Model.MBits0 = iModelBits;
        var w = new BitWriter();
        var cbphp = MbHp.EncodeLumaMb(w, encState, mbhpMode: 0, trimFlexBits: 0, mb);
        cbphp.ShouldBe(0, "all magnitudes < 1 << iModelBits → no VLC nonzero → CBPHP empty");

        var decState = new MbHpState();
        decState.Model.MBits0 = iModelBits;
        var r = new BitReader(w.AsSpan());
        var decoded = new int[256];
        MbHp.DecodeLumaMb(ref r, decState, mbhpMode: 0, trimFlexBits: 0, cbphp, decoded);

        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
                decoded[b * 16 + p].ShouldBe(mb[b * 16 + p], $"block {b} pos {p}");
    }

    /// <summary>
    /// Empty MB with iModelBits > 0 emits only FlexBits zero-bits and round-trips.
    /// </summary>
    [Fact]
    public void EmptyMb_WithIModelBits_StillRoundTrips()
    {
        const int iModelBits = 3;
        var mb = new int[256];

        var encState = new MbHpState();
        encState.Model.MBits0 = iModelBits;
        var w = new BitWriter();
        var cbphp = MbHp.EncodeLumaMb(w, encState, mbhpMode: 0, trimFlexBits: 0, mb);
        cbphp.ShouldBe(0);

        // FlexBits still emitted: 16 blocks * 15 positions * iModelBits = 16*15*3 = 720 bits.
        w.BitPosition.ShouldBe(16 * 15 * iModelBits);

        var decState = new MbHpState();
        decState.Model.MBits0 = iModelBits;
        var r = new BitReader(w.AsSpan());
        var decoded = new int[256];
        MbHp.DecodeLumaMb(ref r, decState, mbhpMode: 0, trimFlexBits: 0, cbphp, decoded);

        for (var b = 0; b < 16; b++)
            for (var p = 1; p < 16; p++)
                decoded[b * 16 + p].ShouldBe(0);
    }

    /// <summary>
    /// Multi-component (YUV444, 3 components) round-trip with separate
    /// luma/chroma iModelBits values.
    /// </summary>
    [Fact]
    public void MultiComponent_YUV444_RoundTrip_WithSplitIModelBits()
    {
        const int iModelBitsLum = 4;
        const int iModelBitsChr = 2;
        var rng = new Random(0x21B5);
        var mb = new int[3 * 256];
        for (var c = 0; c < 3; c++)
            for (var b = 0; b < 16; b++)
                for (var p = 1; p < 16; p++)
                    if (rng.Next(0, 4) == 0)
                        mb[c * 256 + b * 16 + p] = rng.Next(-100, 101);

        var encState = new MbHpState();
        encState.Model.MBits0 = iModelBitsLum;
        encState.Model.MBits1 = iModelBitsChr;
        var w = new BitWriter();
        var cbphp = new int[3];
        MbHp.EncodeMb(w, encState, mbhpMode: 0,
            JxrInternalColorFormat.YUV444, numComponents: 3,
            trimFlexBits: 0, mb, cbphp);

        var decState = new MbHpState();
        decState.Model.MBits0 = iModelBitsLum;
        decState.Model.MBits1 = iModelBitsChr;
        var r = new BitReader(w.AsSpan());
        var decoded = new int[3 * 256];
        MbHp.DecodeMb(ref r, decState, mbhpMode: 0,
            JxrInternalColorFormat.YUV444, numComponents: 3,
            trimFlexBits: 0, cbphp, decoded);

        for (var c = 0; c < 3; c++)
            for (var b = 0; b < 16; b++)
                for (var p = 1; p < 16; p++)
                    decoded[c * 256 + b * 16 + p].ShouldBe(mb[c * 256 + b * 16 + p],
                        $"c={c} block={b} pos={p}");
    }

    /// <summary>
    /// ComputeCbphpWithSplit must match the CBPHP that EncodeMb produces.
    /// </summary>
    [Fact]
    public void ComputeCbphpWithSplit_MatchesEncoderOutput()
    {
        const int iModelBitsLum = 3;
        const int iModelBitsChr = 5;
        var rng = new Random(0x21B6);
        var mb = new int[3 * 256];
        for (var c = 0; c < 3; c++)
            for (var b = 0; b < 16; b++)
                for (var p = 1; p < 16; p++)
                    if (rng.Next(0, 4) == 0)
                        mb[c * 256 + b * 16 + p] = rng.Next(-200, 201);

        var precomputed = new int[3];
        MbHp.ComputeCbphpWithSplit(numComponents: 3,
            iModelBitsLum, iModelBitsChr, mb, precomputed);

        var encState = new MbHpState();
        encState.Model.MBits0 = iModelBitsLum;
        encState.Model.MBits1 = iModelBitsChr;
        var w = new BitWriter();
        var emitted = new int[3];
        MbHp.EncodeMb(w, encState, mbhpMode: 0,
            JxrInternalColorFormat.YUV444, numComponents: 3,
            trimFlexBits: 0, mb, emitted);

        for (var c = 0; c < 3; c++)
            precomputed[c].ShouldBe(emitted[c], $"c={c}");
    }

    /// <summary>
    /// Multi-MB sequence: feed three random MBs through new EncodeLumaMb +
    /// DecodeLumaMb so the CoefficientModel adapts iModelBits via Update.
    /// The model state on encode and decode must stay in lock-step.
    /// </summary>
    [Fact]
    public void MultiMb_ModelAdapts_LockstepBetweenEncoderAndDecoder()
    {
        var rng = new Random(0x21B7);
        var mbs = new[]
        {
            BuildMb(seed: 0xAAAA),
            BuildMb(seed: 0xBBBB),
            BuildMb(seed: 0xCCCC),
        };

        var encState = new MbHpState();
        // Manually bump initial iModelBits to exercise the split from MB 0.
        encState.Model.MBits0 = 4;

        var w = new BitWriter();
        var cbphps = new int[mbs.Length];
        for (var k = 0; k < mbs.Length; k++)
            cbphps[k] = MbHp.EncodeLumaMb(w, encState, mbhpMode: 0, trimFlexBits: 0, mbs[k]);

        var decState = new MbHpState();
        decState.Model.MBits0 = 4;
        var r = new BitReader(w.AsSpan());

        for (var k = 0; k < mbs.Length; k++)
        {
            var decoded = new int[256];
            MbHp.DecodeLumaMb(ref r, decState, mbhpMode: 0, trimFlexBits: 0, cbphps[k], decoded);
            for (var b = 0; b < 16; b++)
                for (var p = 1; p < 16; p++)
                    decoded[b * 16 + p].ShouldBe(mbs[k][b * 16 + p],
                        $"MB {k}, block {b}, pos {p}");
        }

        decState.Model.MBits0.ShouldBe(encState.Model.MBits0,
            "encoder and decoder must land at the same iModelBits after equivalent MB updates");
        decState.Model.MState0.ShouldBe(encState.Model.MState0);
    }
}
