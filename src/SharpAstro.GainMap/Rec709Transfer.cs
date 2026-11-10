namespace SharpAstro.GainMap;

/// <summary>
/// The ITU-R BT.709 transfer function (the same curve BT.601 and BT.2020 use at 10 bits), in double precision.
/// Apple's HDR gain map is "encoded using the Rec.709 transfer function" and is linearised by inverting it
/// (<c>developer.apple.com/documentation/appkit/applying-apple-hdr-effect-to-your-photos</c>), which is why it is here:
/// <see cref="GainMapReconstructor.ReconstructAppleFromLinear"/> needs it, and the HEIF package should not carry its own.
/// The constants are BT.709's exact ones (alpha 1.0992968268..., beta 0.01805396851...), the same as
/// <c>HdrTransfer.Bt709ToLinear</c> in SharpAstro.Codecs.Abstractions uses for the HEVC base image.
/// </summary>
public static class Rec709Transfer
{
    private const double Alpha = 1.0992968268094429;
    private const double Beta = 0.018053968510807;
    private const double EncodedKnee = 4.5 * Beta;

    /// <summary>EOTF-style decode: a Rec.709 encoded value in [0, 1] to linear light in [0, 1].</summary>
    public static double ToLinear(double encoded) =>
        encoded < EncodedKnee ? encoded / 4.5 : Math.Pow((encoded + (Alpha - 1)) / Alpha, 1 / 0.45);
}
