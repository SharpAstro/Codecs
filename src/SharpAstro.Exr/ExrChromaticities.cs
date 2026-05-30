namespace SharpAstro.Exr;

/// <summary>
/// The OpenEXR <c>chromaticities</c> header attribute: the CIE xy coordinates of the
/// red/green/blue primaries and the white point that define the colour space of the
/// RGB channels. Optional — when absent, OpenEXR readers assume Rec.709 / sRGB
/// primaries (<see cref="Rec709"/>). On disk it is eight little-endian 32-bit floats
/// in the order red.x, red.y, green.x, green.y, blue.x, blue.y, white.x, white.y.
/// </summary>
public readonly record struct ExrChromaticities(
    float RedX, float RedY,
    float GreenX, float GreenY,
    float BlueX, float BlueY,
    float WhiteX, float WhiteY)
{
    /// <summary>Rec.709 / sRGB primaries with the D65 white point — the OpenEXR default
    /// when the attribute is absent, and the right tag for sRGB-referred RGB data.</summary>
    public static ExrChromaticities Rec709 => new(
        RedX: 0.6400f, RedY: 0.3300f,
        GreenX: 0.3000f, GreenY: 0.6000f,
        BlueX: 0.1500f, BlueY: 0.0600f,
        WhiteX: 0.3127f, WhiteY: 0.3290f);
}
