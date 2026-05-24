namespace SharpAstro.Png;

/// <summary>
/// Optional metadata for <see cref="PngWriter"/>. All fields default to
/// "don't emit the chunk"; passing a populated <see cref="PngWriteOptions"/>
/// to one of the <c>Encode(..., PngWriteOptions)</c> overloads adds the
/// corresponding ancillary chunks to the PNG output, in the chunk-order
/// PNG spec §5.6 requires (most ancillary chunks before IDAT; eXIf may be
/// before or after IDAT, we emit before).
/// </summary>
public sealed record PngWriteOptions
{
    /// <summary>
    /// Raw ICC profile bytes. When non-null, an <c>iCCP</c> chunk is
    /// emitted with <see cref="IccProfileName"/> as the keyword. Use
    /// <c>SharpAstro.Color.Icc.IccProfiles.SRgbV4</c> for a pre-bundled
    /// sRGB v4 profile.
    /// </summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Keyword for the <c>iCCP</c> chunk; defaults to "ICC profile" (matches libpng / Adobe convention).</summary>
    public string IccProfileName { get; init; } = "ICC profile";

    /// <summary>
    /// When set, emit an <c>sRGB</c> chunk declaring the rendering intent.
    /// Per PNG spec: 0=Perceptual, 1=Relative Colorimetric, 2=Saturation,
    /// 3=Absolute Colorimetric. The spec also says <c>iCCP</c> and
    /// <c>sRGB</c> are mutually exclusive — if both are populated, only
    /// <c>iCCP</c> is emitted (with a warning suppressed; calling code is
    /// expected to pick one).
    /// </summary>
    public byte? SrgbRenderingIntent { get; init; }

    /// <summary>
    /// When set, emit a <c>gAMA</c> chunk with this image gamma (e.g.
    /// 0.45455 for sRGB-style 1/2.2). PNG stores it as a u32 of
    /// <c>round(gamma × 100000)</c>.
    /// </summary>
    public double? Gamma { get; init; }

    /// <summary>When set, emit a <c>cHRM</c> chunk with the supplied primaries + white point.</summary>
    public ChromaticityChunk? Chromaticity { get; init; }

    /// <summary>When set, emit an <c>eXIf</c> chunk with the raw EXIF blob.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>
    /// When set, emit a PNG-3 <c>cICP</c> chunk — Coding-Independent Code
    /// Points declaring color primaries + transfer function. This is how
    /// PNG-3 signals HDR (e.g. <see cref="CicpChunk.Hdr10Pq"/>).
    /// </summary>
    public CicpChunk? Cicp { get; init; }

    /// <summary>When set, emit a PNG-3 <c>mDCv</c> Mastering Display Color Volume chunk.</summary>
    public MdcvChunk? Mdcv { get; init; }

    /// <summary>When set, emit a PNG-3 <c>cLLI</c> Content Light Level Information chunk.</summary>
    public ClliChunk? Clli { get; init; }
}
