using System;
using System.Diagnostics.CodeAnalysis;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Exr;
using SharpAstro.Jpeg;
using SharpAstro.Jxl;
using SharpAstro.Jxr;
using SharpAstro.Png;
using SharpAstro.Tiff;

namespace SharpAstro.Codecs;

/// <summary>
/// The image-codec facade: sniff a byte stream's format by its leading magic-byte
/// signature and dispatch to the matching SharpAstro codec. One reference to this
/// package pulls the whole codec family in lockstep; consumers call these entry
/// points instead of hard-wiring per-format decoders.
/// <para>
/// Registers PNG, JPEG, TIFF, JPEG XR, OpenEXR, and JPEG XL. To add a format,
/// implement <see cref="IImageDecoder"/> in its codec package and add a
/// <c>Register&lt;T&gt;()</c> line to <see cref="_registry"/> - the order is the
/// sniff order (TIFF's <c>II*\0</c>/<c>MM\0*</c> and JXR's <c>II\xBC</c> differ
/// at byte 2, so the two II-prefixed sniffs never collide).
/// </para>
/// <para>
/// Float-sample content (EXR, HDR float JXR/JXL/TIFF) decodes through
/// <see cref="TryDecode"/> with <see cref="IDecodedImage.ColorEncoding"/>
/// declaring its meaning; <see cref="TryDecodeIntoRgba8"/> serves the
/// integer-sample display path only - projecting HDR float to 8 bits is a
/// consumer tone/stretch decision, not a codec one.
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
        Register<JpegImageDecoder>(),
        Register<TiffImageDecoder>(),
        Register<JxrImageDecoder>(),
        Register<ExrImageDecoder>(),
        Register<JxlImageDecoder>(),
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
    /// True if any registered codec recognises the stream's leading bytes. Does not
    /// validate the full payload - a positive result only means the signature matched.
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
