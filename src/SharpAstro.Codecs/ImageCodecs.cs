using System;
using System.Diagnostics.CodeAnalysis;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Bmp;
using SharpAstro.Exr;
using SharpAstro.Gif;
using SharpAstro.Heif;
using SharpAstro.Jbig2;
using SharpAstro.Jpeg;
using SharpAstro.Jpeg2000;
using SharpAstro.Jxl;
using SharpAstro.Jxr;
using SharpAstro.Png;
using SharpAstro.Pnm;
using SharpAstro.Radiance;
using SharpAstro.Tga;
using SharpAstro.Tiff;
using SharpAstro.Webp;

namespace SharpAstro.Codecs;

/// <summary>
/// The image-codec facade: sniff a byte stream's format by its leading magic-byte
/// signature and dispatch to the matching SharpAstro codec. One reference to this
/// package pulls the whole codec family in lockstep; consumers call these entry
/// points instead of hard-wiring per-format decoders.
/// <para>
/// Registers PNG, JPEG, TIFF, JPEG XR, OpenEXR, JPEG XL, JBIG2, JPEG 2000, WebP, HEIF, GIF, Radiance RGBE, Netpbm, BMP and TGA. To add a
/// format, implement <see cref="IImageDecoder"/> in its codec package and add a
/// <c>Register&lt;T&gt;()</c> line to <see cref="_registry"/> - the order is the
/// sniff order (TIFF's <c>II*\0</c>/<c>MM\0*</c> and JXR's <c>II\xBC</c> differ
/// at byte 2, so the two II-prefixed sniffs never collide).
/// </para>
/// <para>
/// JBIG2 is registered for standalone <c>.jb2</c> files only. Its real use -
/// PDF's <c>/JBIG2Decode</c> filter - cannot come through here at all: an
/// embedded JBIG2 stream has no file header to sniff, keeps its segment
/// dictionaries in a separate <c>/JBIG2Globals</c> stream, and takes its
/// dimensions from the image dictionary. Those callers use
/// <c>Jbig2Decoder.Decode(embedded, globals, width, height)</c> directly.
/// </para>
/// <para>
/// JPEG 2000 is registered for the images whose samples are grey or RGB once
/// decoded: one or three components, not sYCC. Its JP2 and raw-J2K signatures
/// collide with nothing above. What the facade cannot present (alpha, CMYK,
/// sYCC) is refused rather than guessed, and <c>Jpeg2000Decoder.Decode</c>
/// still hands back every component, with the JP2 colour box reported.
/// </para>
/// <para>
/// Radiance (<c>.hdr</c> / <c>.pic</c>) decodes to three-channel scene-linear float; an XYZE file is tagged
/// <c>ColorPrimaries.SMPTE428</c> (CIE XYZ) rather than presented as RGB.
/// </para>
/// <para>
/// Float-sample content (EXR, Radiance, HDR float JXR/JXL/TIFF) decodes through
/// <see cref="TryDecode"/> with <see cref="IDecodedImage.ColorEncoding"/>
/// declaring its meaning; <see cref="TryDecodeIntoRgba8"/> serves the
/// integer-sample display path only - projecting HDR float to 8 bits is a
/// consumer tone/stretch decision, not a codec one.
/// </para>
/// <para>
/// WebP is decoded by <see cref="WebpImageDecoder"/>: lossy (VP8), lossless (VP8L)
/// and, for an animation, its first composited frame. It returns RGB8 or RGBA8
/// according to the container's alpha declaration.
/// </para>
/// <para>
/// BMP is decoded by <see cref="BmpImageDecoder"/>: Windows and OS/2 DIBs at 1 to 32 bits, RLE4/RLE8 and
/// bit-field masks, as RGB8 or, when the file has alpha, RGBA8, with a V5 ICC profile passed through.
/// <c>BI_PNG</c>, <c>BI_JPEG</c> and <c>BI_CMYK</c> files answer false.
/// GIF (<c>GIF87a</c> / <c>GIF89a</c>) is decoded by <see cref="GifImageDecoder"/>: the first frame
/// composited onto the logical screen, always as 8-bit RGBA (transparent pixels have alpha 0). A
/// truncated file decodes what is there; <c>GifDecoder</c> reports that and the animation structure,
/// and <c>GifDecoder.DecodeAnimation</c> / <c>GifAnimationDecoder</c> give every composited frame.
/// </para>
/// <para>
/// Netpbm (<c>P1</c>..<c>P7</c>: PBM, PGM, PPM and PAM, plain and raw) is decoded by
/// <see cref="PnmImageDecoder"/> to grey, grey+alpha, RGB or RGBA, UInt8 when the file's maxval
/// is at most 255 and UInt16 above, with samples scaled to fill the format. It is sniffed by
/// <c>P</c>, a digit 1-7 and a whitespace byte.
/// </para>
/// <para>
/// TGA is the one format recognised by its <b>end</b> rather than its start. A TGA has no magic number, so a
/// header guess would claim arbitrary bytes; <see cref="TgaImageDecoder"/> instead requires the TGA 2.0
/// footer (<c>TRUEVISION-XFILE.\0</c>, the last 18 bytes) and a header <c>TgaReader</c> accepts. It is
/// registered last, and a footerless (TGA 1.0) file is not recognised here: decode it with
/// <c>TgaReader.Decode</c> directly. Opaque TGAs decode as RGB8, ones with alpha as RGBA8.
/// </para>
/// <para>
/// A gain-map ("Ultra HDR") JPEG is recognised too: <see cref="TryDecode"/> +
/// <see cref="IDecodedImage.ToFloats"/> reconstruct the authored HDR (linear,
/// display-referred), while <see cref="TryDecodeIntoRgba8"/> and
/// <see cref="IDecodedImage.ToRgba8"/> return the SDR base rendition - so the
/// float path is HDR and the 8-bit path is the graceful SDR fallback. Plain JPEGs
/// are unaffected.
/// </para>
/// </summary>
public static class ImageCodecs
{
    // ReadOnlySpan<byte> is a ref struct, so it cannot be a Func<> type argument -
    // hence the bespoke delegate types matching IImageDecoder's static members.
    private delegate bool CanDecodeFn(ReadOnlySpan<byte> header);
    private delegate bool TryReadInfoFn(ReadOnlySpan<byte> data, out ImageInfo info);
    private delegate bool TryDecodeFn(ReadOnlySpan<byte> data, out IDecodedImage? image);
    private delegate bool TryDecodeIntoRgba8Fn(ReadOnlySpan<byte> data, Span<byte> rgbaDestination);

    private readonly record struct Entry(
        CanDecodeFn CanDecode,
        TryReadInfoFn TryReadInfo,
        TryDecodeFn TryDecode,
        TryDecodeIntoRgba8Fn TryDecodeIntoRgba8);

    // Explicit registry: IImageDecoder's static-abstract members are not enumerable
    // and reflection-scanning for implementors is not AOT-safe, so each codec is
    // registered by hand. Binding delegates to the static members via a generic
    // instantiation stays fully AOT-friendly.
    private static readonly Entry[] _registry =
    [
        Register<PngImageDecoder>(),
        // GainMap must precede the plain JPEG decoder: both answer the FF D8 FF
        // signature, but GainMapImageDecoder.CanDecode only claims a JPEG that
        // actually carries a locatable gain map, so ordinary JPEGs fall through.
        // It makes ToFloats() reconstruct the authored HDR while the 8-bit path
        // keeps returning the SDR base.
        Register<GainMapImageDecoder>(),
        Register<JpegImageDecoder>(),
        Register<TiffImageDecoder>(),
        Register<JxrImageDecoder>(),
        Register<ExrImageDecoder>(),
        Register<JxlImageDecoder>(),
        Register<Jbig2ImageDecoder>(),
        Register<Jpeg2000ImageDecoder>(),
        Register<WebpImageDecoder>(),
        // HEIF answers only an ISOBMFF 'ftyp' as the FIRST box naming an HEVC image brand (heic/heix/heim/heis),
        // so JPEG 2000's JP2 (a 'jP  ' signature box first), AVIF ('avif' brands) and CR3 ('crx ') never match.
        Register<HeifImageDecoder>(),
        // BMP's signature is two letters, so BmpImageDecoder.CanDecode also checks the DIB header size and
        // the zeroed reserved words; it collides with nothing above.
        Register<BmpImageDecoder>(),
        Register<GifImageDecoder>(),
        // '#?RADIANCE' / '#?RGBE' collides with no binary signature above.
        Register<RadianceImageDecoder>(),
        // Netpbm's "P1".."P7" + whitespace collides with no other signature here.
        Register<PnmImageDecoder>(),
        // Last on purpose: TGA is sniffed by its trailing footer, not a leading magic number, so it
        // can only ever claim a stream that no leading-signature codec above already took.
        Register<TgaImageDecoder>(),
    ];

    private static Entry Register<T>() where T : IImageDecoder =>
        new(T.CanDecode, T.TryReadInfo, T.TryDecode, T.TryDecodeIntoRgba8);

    /// <summary>
    /// Decodes <paramref name="data"/> into a codec-neutral <see cref="IDecodedImage"/>
    /// (fidelity tier - preserves source bit depth and channel count). Returns false
    /// when no registered codec recognises the stream or the payload is undecodable.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return entry.TryDecode(data, out image);
        }

        image = null;
        return false;
    }

    /// <summary>
    /// Reads dimensions + layout from the header without a full decode, so a caller
    /// can size a destination for <see cref="TryDecodeIntoRgba8"/>. Returns false when
    /// no registered codec recognises the stream or the header is malformed.
    /// </summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return entry.TryReadInfo(data, out info);
        }

        info = default;
        return false;
    }

    /// <summary>
    /// Decodes directly into a caller-provided 8-bit RGBA destination (row-major,
    /// 4 bytes/pixel, R,G,B,A byte order) - the zero-copy display hot path.
    /// <paramref name="rgbaDestination"/> must be at least <c>Width * Height * 4</c>
    /// bytes (size it via <see cref="TryReadInfo"/>). Returns false when no codec
    /// recognises the stream, the destination is too small, or the payload is
    /// undecodable.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return entry.TryDecodeIntoRgba8(data, rgbaDestination);
        }

        return false;
    }

    /// <summary>
    /// True if any registered codec recognises the stream (most by its leading bytes; TGA by its footer, so it
    /// needs the whole stream). Does not validate the full payload - a positive result only means the signature matched.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return true;
        }

        return false;
    }
}
