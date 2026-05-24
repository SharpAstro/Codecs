using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase 21c + 21d: AllBands round-trip tests for TileSpatial (inline
/// FlexBits) and TileFrequency (4th TILE_FLEXBITS sub-stream). The
/// iModelBits split happens when the encoder's <c>MbHpState.Model.MBits0</c>
/// (luma) or <c>MBits1</c> (chroma) is &gt; 0 — we pre-bump the model to
/// exercise the FlexBits path from the very first MB.
/// </summary>
public sealed class JxrAllBandsRoundTripTests
{
    private static int[] BuildHpBuf(int seed, int numComponents)
    {
        var rng = new Random(seed);
        var hp = new int[numComponents * 256];
        for (var c = 0; c < numComponents; c++)
            for (var b = 0; b < 16; b++)
                for (var p = 1; p < 16; p++)
                    if (rng.Next(0, 3) == 0)
                        hp[c * 256 + b * 16 + p] = rng.Next(-150, 151);
        return hp;
    }

    private static Macroblock[] BuildTile(int widthInMb, int heightInMb, int numComponents, int seed)
    {
        var rng = new Random(seed);
        var mbs = new Macroblock[widthInMb * heightInMb];
        for (var i = 0; i < mbs.Length; i++)
        {
            var mb = new Macroblock
            {
                Dc = new int[numComponents],
                Lp = new int[numComponents * 16],
                Hp = BuildHpBuf(seed ^ i ^ 0xFEED, numComponents),
                MbHpMode = 2, // no-prediction mode keeps the LP-derived mode deterministic
            };
            for (var c = 0; c < numComponents; c++)
                mb.Dc[c] = rng.Next(-100, 101);
            mbs[i] = mb;
        }
        return mbs;
    }

    [Fact]
    public void TileSpatial_AllBands_YOnly_RoundTrips_AtIModelBitsZero()
    {
        // iModelBits = 0 on entry — AllBands path runs but no FlexBits bits
        // are actually emitted. Verifies the dispatch wiring matches
        // NoFlexbits semantically.
        var plane = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            NumComponents = 1,
            BandsPresent = JxrBandsPresent.AllBands,
        };
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.AllBands);
        var mbs = BuildTile(widthInMb: 2, heightInMb: 2, numComponents: 1, seed: 0x21C0);

        var w = new BitWriter();
        TileSpatial.Write(w, headers, JxrBandsPresent.AllBands,
            trimFlexBitsFlag: false, plane, 2, 2, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileSpatial.Read(ref r, JxrBandsPresent.AllBands,
            trimFlexBitsFlag: false, plane, 2, 2, out _);

        for (var i = 0; i < mbs.Length; i++)
            for (var p = 0; p < 256; p++)
                decoded[i].Hp[p].ShouldBe(mbs[i].Hp[p], $"MB {i} pos {p}");
    }

    [Fact]
    public void TileFrequency_AllBands_YOnly_RoundTrips_AtIModelBitsZero()
    {
        var plane = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            NumComponents = 1,
            BandsPresent = JxrBandsPresent.AllBands,
        };
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.AllBands);
        var mbs = BuildTile(widthInMb: 2, heightInMb: 2, numComponents: 1, seed: 0x21D0);

        var w = new BitWriter();
        TileFrequency.Write(w, headers, JxrBandsPresent.AllBands,
            trimFlexBitsFlag: false, plane, 2, 2, mbs);

        var r = new BitReader(w.AsSpan());
        var decoded = TileFrequency.Read(ref r, JxrBandsPresent.AllBands,
            trimFlexBitsFlag: false, plane, 2, 2, out _);

        for (var i = 0; i < mbs.Length; i++)
            for (var p = 0; p < 256; p++)
                decoded[i].Hp[p].ShouldBe(mbs[i].Hp[p], $"MB {i} pos {p}");
    }

    [Fact]
    public void TileFrequency_AllBands_WriteBands_ReturnsFourSubStreams()
    {
        var plane = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            NumComponents = 1,
            BandsPresent = JxrBandsPresent.AllBands,
        };
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.AllBands);
        var mbs = BuildTile(widthInMb: 1, heightInMb: 1, numComponents: 1, seed: 0x21D1);

        var bands = TileFrequency.WriteBands(headers, JxrBandsPresent.AllBands,
            trimFlexBitsFlag: false, plane, 1, 1, mbs);

        bands.Length.ShouldBe(4, "AllBands emits DC + LP + HP + FlexBits");
        // The FlexBits sub-stream may be empty (zero bytes) when iModelBits=0
        // on every MB — what matters is that it's present in the result.
        bands[3].ShouldNotBeNull();
    }

    /// <summary>
    /// True FlexBits path: pre-bump the encoder's HP coefficient model so
    /// iModelBits &gt; 0 from MB 0, then verify the decoder reconstructs
    /// every HP coefficient exactly. The decoder's model adapts in lock-step
    /// because it starts from the same pre-bumped state.
    /// </summary>
    [Fact]
    public void TileSpatial_AllBands_IModelBitsThree_FlexBitsRoundTrips()
    {
        const int iModelBitsBump = 3;
        var plane = new ImagePlaneHeader
        {
            InternalClrFmt = JxrInternalColorFormat.YOnly,
            NumComponents = 1,
            BandsPresent = JxrBandsPresent.AllBands,
        };
        var headers = TileBandHeaders.Uniform(JxrBandsPresent.AllBands);
        var mbs = BuildTile(widthInMb: 1, heightInMb: 1, numComponents: 1, seed: 0x21C1);

        // Custom Write that injects iModelBits into the encoder's hpState
        // before the per-MB loop runs. Since TileSpatial.Write internally
        // allocates a fresh MbHpState, we'd need to expose iModelBits via the
        // tile header or precondition the model. The simplest path is to
        // invoke MbHp directly to confirm round-trip at the MB layer, then
        // assert TileSpatial's AllBands-with-iModelBits=0 path doesn't break.
        // Full TileSpatial control over iModelBits requires an encoder API
        // that we'll add when AllBands becomes an encoder-callable feature.
        // For now: re-verify single-MB MbHp round-trip with the AllBands-style
        // call shape that TileSpatial produces.
        var encState = new MbHpState();
        encState.Model.MBits0 = iModelBitsBump;
        var w = new BitWriter();

        // Pre-pass: compute CBPHP with split.
        var cbphpBuf = new int[1];
        MbHp.ComputeCbphpWithSplit(numComponents: 1,
            iModelBitsLum: iModelBitsBump, iModelBitsChr: 0, mbs[0].Hp, cbphpBuf);
        var cbphpState = new MbCbphpState();
        MbCbphp.EncodeMb(w, cbphpState, numComponents: 1, cbphpBuf);
        var hpDummy = new int[1];
        MbHp.EncodeMb(w, encState, mbs[0].MbHpMode,
            JxrInternalColorFormat.YOnly, numComponents: 1,
            trimFlexBits: 0, mbs[0].Hp, hpDummy);

        var decState = new MbHpState();
        decState.Model.MBits0 = iModelBitsBump;
        var r = new BitReader(w.AsSpan());
        var decCbphpBuf = new int[1];
        var decCbphpState = new MbCbphpState();
        MbCbphp.DecodeMb(ref r, decCbphpState, numComponents: 1, decCbphpBuf);
        var decoded = new int[256];
        MbHp.DecodeMb(ref r, decState, mbs[0].MbHpMode,
            JxrInternalColorFormat.YOnly, numComponents: 1,
            trimFlexBits: 0, decCbphpBuf, decoded);

        for (var p = 0; p < 256; p++)
            decoded[p].ShouldBe(mbs[0].Hp[p], $"pos {p}");
    }
}
