using System.IO.Compression;

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

    /// <summary>
    /// Write RGB (PNG colour type 2) rather than RGBA, discarding the alpha channel of an RGBA
    /// input. Ignored by the grayscale and packed-RGB entry points, which have no alpha to drop.
    /// </summary>
    /// <remarks>
    /// <para>A render with no transparency -- an astronomical frame, a chart, anything already
    /// composited against its background -- still carries a constant-0xFF(FF) alpha plane, and that
    /// plane is filtered, scored, deflated and stored exactly like real data. Dropping it removes a
    /// quarter of the encoder's per-byte work as well as a quarter of the bytes handed to deflate,
    /// so this is a speed option at least as much as a size one.</para>
    /// <para>The caller asserts the image is opaque: the alpha samples are not examined, because
    /// discovering a transparent pixel halfway through a multi-second encode is no more useful than
    /// not having looked. When the source is already packed RGB, use
    /// <see cref="PngWriter.EncodeRgb8"/> / <see cref="PngWriter.EncodeRgb16"/> instead.</para>
    /// </remarks>
    public bool DiscardAlpha { get; init; }

    /// <summary>
    /// Deflate level for the IDAT stream. Defaults to <see cref="CompressionLevel.Optimal"/> (zlib
    /// level 6), which is what every caller got before this was settable.
    /// </summary>
    /// <remarks>
    /// Deflate is the single largest term in a large encode, so this is the one knob that moves it
    /// without changing a pixel. It is exposed rather than re-defaulted because the trade belongs to
    /// the caller: an interactive "save this frame" wants the seconds back, an archival write does
    /// not.
    /// </remarks>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;
}
