using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Round-trip tests for the FlexBits primitive (T.832 §8.7.19.3 DECODE_FLEX).
/// </summary>
public sealed class BlockFlexBitsTests
{
    [Theory]
    [InlineData(1, 0, 5)]      // VLC positive, refinement positive
    [InlineData(1, 0, 0)]      // VLC positive, refinement zero
    [InlineData(-1, 0, 5)]     // VLC negative, refinement positive (negated on decode)
    [InlineData(0, 1, 7)]      // VLC zero, refinement positive → emits SIGN_FLAG
    [InlineData(0, -1, 11)]    // VLC zero, refinement negative → emits SIGN_FLAG
    [InlineData(0, 0, 0)]      // VLC zero, refinement zero → no SIGN_FLAG
    public void EncodeOne_DecodeOne_RoundTrips(int vlcValue, int signSentinel, int absRef)
    {
        // The encoder takes a "signed refinement"; for VLC>0 the refinement's
        // sign is inherited from VLC, and for VLC<0 the refinement is negated.
        // For VLC==0 the encoder needs the signed refinement directly.
        int signedRef = signSentinel switch
        {
            0 when vlcValue > 0 => absRef,
            0 when vlcValue < 0 => -absRef,
            _ => signSentinel * absRef
        };
        const int iFlexBitsLeft = 4;
        var w = new BitWriter();
        BlockFlexBits.EncodeOne(w, vlcValue, signedRef, iFlexBitsLeft);

        var r = new BitReader(w.AsSpan());
        var decoded = BlockFlexBits.DecodeOne(ref r, vlcValue, iFlexBitsLeft);
        decoded.ShouldBe(signedRef, $"vlc={vlcValue} ref={signedRef}");
    }

    [Fact]
    public void EncodeOne_ZeroFlexBits_NoOutput()
    {
        var w = new BitWriter();
        BlockFlexBits.EncodeOne(w, vlcValue: 0, signedRefinement: 0, iFlexBitsLeft: 0);
        w.BitPosition.ShouldBe(0);
    }

    [Fact]
    public void DecodeOne_ZeroFlexBits_NoConsume()
    {
        var r = new BitReader(new byte[8]);
        var v = BlockFlexBits.DecodeOne(ref r, vlcValue: 5, iFlexBitsLeft: 0);
        v.ShouldBe(0);
        r.BitPosition.ShouldBe(0);
    }

    [Fact]
    public void EncodeBlock_DecodeBlock_RoundTrips_FullPattern()
    {
        // VLC values + refinements for the 15 AC positions. Mix of positive,
        // negative, zero VLC values to exercise the sign-handling branches.
        var rng = new Random(unchecked((int)0xF1E1B175));
        var vlc = new int[15];
        var refData = new int[15];
        for (var i = 0; i < 15; i++)
        {
            vlc[i] = i switch
            {
                0 or 5 or 10 => 0,         // some VLC-zero positions
                1 or 2 or 3 => -(i * 2),   // some negative
                _ => i * 3                 // rest positive
            };
            var sign = (vlc[i] != 0) ? Math.Sign(vlc[i]) : (rng.Next(2) == 0 ? 1 : -1);
            var absRef = rng.Next(0, 16);
            refData[i] = sign * absRef;
        }

        var w = new BitWriter();
        BlockFlexBits.EncodeBlock(w, vlc, refData, iFlexBitsLeft: 4);
        var r = new BitReader(w.AsSpan());
        var decoded = new int[15];
        BlockFlexBits.DecodeBlock(ref r, vlc, decoded, iFlexBitsLeft: 4);
        for (var i = 0; i < 15; i++)
            decoded[i].ShouldBe(refData[i], $"position {i}");
    }
}
