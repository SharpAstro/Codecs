using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Component tests for <see cref="ChromaTransform"/> — the reduced-resolution chroma
/// Photo Core Transform at OL_NONE. Built encoder+decoder together (the port discipline):
/// forward (encode) then inverse (decode) must be the exact integer identity, proving the
/// 420 (4-block) / 422 (8-block, incl. the 1-D Hadamard step) stage structure is a true
/// mutual inverse. (Self-consistency only — byte-exactness vs jxrlib is the C5 oracle check.)
/// </summary>
public sealed class JxrChromaTransformTests
{
    [Fact]
    public void Chroma420_ForwardThenInverse_IsIdentity() => RoundTrip(ColorFormat.Yuv420, 64);

    [Fact]
    public void Chroma422_ForwardThenInverse_IsIdentity() => RoundTrip(ColorFormat.Yuv422, 128);

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

        ChromaTransform.ForwardMbNoOverlap(plane, 0, cf);
        plane.ShouldNotBe(orig); // sanity: the forward transform actually changed the data
        ChromaTransform.InverseMbNoOverlap(plane, 0, cf);

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
        ChromaTransform.ForwardMbNoOverlap(plane, mbIndex * stride, cf);
        ChromaTransform.InverseMbNoOverlap(plane, mbIndex * stride, cf);

        plane.ShouldBe(orig); // MB 1 round-trips; MBs 0 and 2 are never addressed
    }
}
