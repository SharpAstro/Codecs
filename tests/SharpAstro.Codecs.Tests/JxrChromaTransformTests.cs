using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Component tests for <see cref="ChromaTransform"/> — the reduced-resolution chroma
/// Photo Core Transform at OL_NONE. YUV420/422 always run in jxrlib's <b>scaled-arithmetic</b>
/// mode, where the chroma second stage is the matched <c>strDCT2x2dnEnc</c> (×½-prescale,
/// forward) / <c>strDCT2x2dnDec</c> (×2-postscale, inverse) pair. That pair is an exact mutual
/// inverse in the <b>inverse→forward</b> (decode→re-encode) direction — the ×2 makes the block
/// DCs even so the forward's >>1 recovers them losslessly — but NOT forward→inverse (the >>1
/// drops a bit, as 4:2:0/4:2:2 chroma is inherently lossy). So we round-trip inverse∘forward.
/// (Self-consistency only — byte-exactness vs jxrlib is the C5 oracle check.)
/// </summary>
public sealed class JxrChromaTransformTests
{
    [Fact]
    public void Chroma420_InverseThenForward_IsIdentity() => RoundTrip(ColorFormat.Yuv420, 64);

    [Fact]
    public void Chroma422_InverseThenForward_IsIdentity() => RoundTrip(ColorFormat.Yuv422, 128);

    [Fact]
    public void Chroma420_RoundTrips_AtNonZeroBase_LeavesNeighboursUntouched() => RoundTripMulti(ColorFormat.Yuv420, 64);

    [Fact]
    public void Chroma422_RoundTrips_AtNonZeroBase_LeavesNeighboursUntouched() => RoundTripMulti(ColorFormat.Yuv422, 128);

    private static void RoundTrip(ColorFormat cf, int stride)
    {
        var rng = new Random(1234);
        var orig = new int[stride];
        for (var i = 0; i < stride; i++) orig[i] = rng.Next(-1024, 1024);
        var plane = (int[])orig.Clone();

        ChromaTransform.InverseMbNoOverlap(plane, 0, cf);
        plane.ShouldNotBe(orig); // sanity: the inverse transform actually changed the data
        ChromaTransform.ForwardMbNoOverlap(plane, 0, cf);

        plane.ShouldBe(orig);
    }

    private static void RoundTripMulti(ColorFormat cf, int stride)
    {
        var rng = new Random(99);
        const int mbs = 3;
        var orig = new int[stride * mbs];
        for (var i = 0; i < orig.Length; i++) orig[i] = rng.Next(-1024, 1024);
        var plane = (int[])orig.Clone();

        const int mbIndex = 1; // transform only the middle MB
        ChromaTransform.InverseMbNoOverlap(plane, mbIndex * stride, cf);
        ChromaTransform.ForwardMbNoOverlap(plane, mbIndex * stride, cf);

        plane.ShouldBe(orig); // MB 1 round-trips; MBs 0 and 2 are never addressed
    }
}
