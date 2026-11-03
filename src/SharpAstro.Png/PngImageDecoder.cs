using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Png;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for PNG, bridging <see cref="PngReader"/>
/// into the <c>SharpAstro.Codecs</c> facade.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>): greyscale / RGB / greyscale+alpha /
/// RGBA preserve their native channel count and bit depth (16-bit is converted from
/// PNG's big-endian to the host byte order required by <see cref="IDecodedImage"/>);
/// indexed images are palette-expanded to 8-bit RGBA (with tRNS alpha when present).
/// Colour signalling survives the adapter: cICP (and gAMA 1.0 → linear) maps onto
/// <see cref="IDecodedImage.ColorEncoding"/>, so PNG-3 HDR tagging isn't dropped
/// at the facade boundary.
/// </para>
/// <para>
/// PNG-only extras, outside <see cref="IImageDecoder"/>: <see cref="TryDecodeIntoRgba8"/> /
/// <see cref="TryDecodeRgba8"/> are the 8-bit display projections (they refuse cICP PQ/HLG), and
/// <see cref="TryDecodeRgbaFloat(ReadOnlySpan{byte}, out float[], out int, out int, out ColorEncoding)"/>
/// is the linear-light float projection that does decode PQ and HLG.
/// </para>
/// </summary>
public sealed class PngImageDecoder : IImageDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <inheritdoc />
    public static int SignatureLength => 8;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(Signature);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        // 8 signature + 4 length + 4 "IHDR" + 13 IHDR payload = 33 bytes minimum.
        if (data.Length < 33 || !CanDecode(data)) return false;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;

        var width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        int bitDepth = data[24];
        int colorType = data[25];
        if (width <= 0 || height <= 0) return false;
        if (!TryMapLayout(colorType, bitDepth, out var channels, out var format)) return false;

        info = new ImageInfo(width, height, channels, format);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        if (!PngReader.TryDecode(data, out var png)) return false;
        try
        {
            image = ToDecoded(png);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>False for cICP PQ/HLG-tagged files: their integer code values are
    /// encoded for an HDR EOTF, so the code-value expansion would render them
    /// crushed/wrong-gamut - the same "no canonical 8-bit projection" refusal the
    /// float codecs make. Use <see cref="TryDecode"/> + a colour-managed
    /// conversion instead, or - for linear-light floats straight from the PNG -
    /// <see cref="TryDecodeRgbaFloat(ReadOnlySpan{byte}, out float[], out int, out int, out ColorEncoding)"/>,
    /// which does decode PQ and HLG. Untagged, sRGB-tagged, and gAMA-linear files keep the
    /// display path (linear is monotone and the astro-master quick-look case;
    /// HLG's nominal SDR fallback was considered, but the facade can't know the
    /// display context, so HLG refuses alongside PQ).</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        if (!TryReadDisplayReferred(data, out var png)) return false;
        if (rgbaDestination.Length < (long)png.Width * png.Height * 4) return false;
        try
        {
            png.ExpandToRgba8(rgbaDestination);
            return true;
        }
        catch (InvalidDataException)
        {
            // An indexed pixel past the palette's end.
            return false;
        }
    }

    /// <summary>
    /// Decode a PNG to tightly-packed 8-bit RGBA in an array the caller owns, without knowing its
    /// size first: the one-call form of <see cref="TryReadInfo"/> + allocate + <see cref="TryDecodeIntoRgba8"/>.
    /// An 8-bit RGBA file's decoded samples already are that layout, so they are handed back as they
    /// are; every other colour type is expanded once, the same mapping <see cref="TryDecodeIntoRgba8"/>
    /// uses. Either way the result is a fresh array of exactly <c>width * height * 4</c> bytes that
    /// nothing else references, so a caller can adopt it (DIR.Lib's <c>RgbaImage(width, height, pixels)</c>)
    /// rather than copy it.
    /// </summary>
    /// <remarks>False for a stream this reader rejects (a corrupt file, an unknown interlace method) and, like <see cref="TryDecodeIntoRgba8"/>, for cICP PQ/HLG-tagged files; those
    /// decode through <see cref="TryDecodeRgbaFloat(ReadOnlySpan{byte}, out float[], out int, out int, out ColorEncoding)"/>.</remarks>
    public static bool TryDecodeRgba8(ReadOnlySpan<byte> data, [NotNullWhen(true)] out byte[]? rgba, out int width, out int height)
    {
        rgba = null;
        width = height = 0;
        if (!TryReadDisplayReferred(data, out var png)) return false;
        try
        {
            rgba = png.ColorType == 6 && png.BitDepth == 8 ? png.Pixels : png.ToRgba8();
        }
        catch (InvalidDataException)
        {
            // An indexed pixel past the palette's end.
            return false;
        }
        width = png.Width;
        height = png.Height;
        return true;
    }

    /// <summary>
    /// <see cref="TryDecodeRgbaFloat(ReadOnlySpan{byte}, out float[], out int, out int, out ColorEncoding, out MdcvChunk, out ClliChunk)"/>
    /// without the <c>mDCv</c> / <c>cLLI</c> metadata.
    /// </summary>
    public static bool TryDecodeRgbaFloat(ReadOnlySpan<byte> data, [NotNullWhen(true)] out float[]? rgba, out int width, out int height,
        [NotNullWhen(true)] out ColorEncoding? encoding)
        => TryDecodeRgbaFloat(data, out rgba, out width, out height, out encoding, out _, out _);

    /// <summary>
    /// Decode a PNG to linear-light floating-point RGBA in an array the caller owns: the HDR projection
    /// that <see cref="TryDecodeRgba8"/> refuses to make. The result is a fresh <c>float[]</c> of exactly
    /// <c>width * height * 4</c> samples, row-major, top-down, R G B A per pixel, that nothing else references.
    /// <para>
    /// <b>What it does.</b> Starts from <see cref="PngReader.TryDecode"/> - so PQ and HLG cICP files are
    /// accepted, not refused - expands each colour sample to a normalised signal (full range
    /// <c>code / (2^n − 1)</c>, or the H.273 §8.3 narrow-range 16..235 form scaled to the bit depth, per the
    /// cICP <c>VideoFullRangeFlag</c>), and linearises it with the transfer the file declares. Greyscale
    /// is replicated into R = G = B; indexed images are expanded through their palette (and <c>tRNS</c>);
    /// every colour type and depth <see cref="PngReader"/> reads is covered, sub-byte ones by the reader's own
    /// unpacking to 8 bits. The curves themselves are in <c>PngHdr</c>.
    /// </para>
    /// <para>
    /// <b>Which transfer.</b> A <c>cICP</c> chunk is authoritative, as in <see cref="TryDecode"/>. Without one,
    /// <c>gAMA</c> 1.0 reads as linear and everything else (untagged, <c>sRGB</c>, other <c>gAMA</c>) as sRGB,
    /// the same assumption the facade's <see cref="IDecodedImage.ColorEncoding"/> makes; an <c>iCCP</c> profile
    /// is not applied (read it from <see cref="PngReader"/> if you need it).
    /// </para>
    /// <para>
    /// <b>Scale.</b> For PQ and HLG, <c>1.0</c> is the 203 cd/m² HDR reference white of ITU-R BT.2408: PQ
    /// light is absolute (code 1.0 = 10 000 cd/m² = 49.26 here) and HLG is rendered through the BT.2100 OOTF
    /// for the nominal 1000 cd/m² display (system gamma 1.2, luminance from the cICP primaries), so a 75 %
    /// HLG signal is 1.0 and a 100 % HLG white is about 4.93. For the SDR transfers (sRGB, BT.709 / 601 / 2020)
    /// and Linear, <c>1.0</c> is SDR white and nothing is rescaled to 203. Narrow-range footroom and headroom
    /// are clamped to the signal's [0, 1] before the curve, except for Linear, which passes through as it is.
    /// Alpha is the file's own straight (not premultiplied) alpha, 0..1, and <c>1.0</c> when the image has none.
    /// </para>
    /// <para>
    /// <b>Returns false</b> for a stream <see cref="PngReader.TryDecode"/> rejects (not a PNG, corrupt,
    /// an unknown interlace method); for a transfer this projection cannot linearise - anything but PQ (16), HLG (18), sRGB (13),
    /// the BT.709 family (1, 6, 14, 15) and Linear (8), so BT.470 gammas, SMPTE 240M, the log curves and
    /// <see cref="TransferFunction.Unspecified"/> are refused rather than guessed; for a cICP chunk whose
    /// matrix coefficients are not <see cref="MatrixCoefficients.Identity"/> (PNG carries RGB, so samples
    /// that claim to be YCbCr are not ours to read as RGB); for a narrow-range grey image of fewer than 8
    /// bits (H.273's range equations start at 8); for an indexed pixel past the palette's end; and for an
    /// image whose float raster would not fit an array.
    /// </para>
    /// <para>
    /// <b>The returned <paramref name="encoding"/></b> carries the file's primaries (cICP, else BT.709) and
    /// says what the floats are: <see cref="TransferFunction.Linear"/>, <see cref="ColorEncoding.FullRange"/>
    /// (the range is already expanded), and <see cref="FloatSemantics.DisplayReferred"/> - <c>1.0</c> is the
    /// reference / SDR white described above, with HDR highlights above it. A Linear-tagged file
    /// (cICP 8 or <c>gAMA</c> 1.0) lands here too: its full-scale code is 1.0, so it reads as display-referred
    /// like the 8-bit display path treats it; a caller who knows it is an astro master may re-interpret it.
    /// </para>
    /// <para>
    /// <b>Why a PNG-specific method.</b> This is deliberately not a facade (<see cref="IImageDecoder"/>)
    /// member: the interface has no float projection today, and widening a static-abstract interface breaks
    /// every codec that implements it. The facade still reaches the integer code values and the colour
    /// signalling through <see cref="TryDecode"/>; a caller that wants PQ / HLG as linear light calls this.
    /// </para>
    /// </summary>
    /// <param name="data">A PNG file.</param>
    /// <param name="rgba">The linear-light RGBA samples, or null when the result is false.</param>
    /// <param name="width">Image width in pixels (0 when false).</param>
    /// <param name="height">Image height in pixels (0 when false).</param>
    /// <param name="encoding">What the floats are (see above), or null when the result is false.</param>
    /// <param name="mdcv">The PNG-3 <c>mDCv</c> mastering-display metadata, or null when the file has none. Carried as it is; it does not change the pixels.</param>
    /// <param name="clli">The PNG-3 <c>cLLI</c> content-light-level metadata, or null when the file has none. Carried as it is; it does not change the pixels.</param>
    public static bool TryDecodeRgbaFloat(ReadOnlySpan<byte> data, [NotNullWhen(true)] out float[]? rgba, out int width, out int height,
        [NotNullWhen(true)] out ColorEncoding? encoding, out MdcvChunk? mdcv, out ClliChunk? clli)
    {
        rgba = null;
        width = height = 0;
        encoding = null;
        mdcv = null;
        clli = null;
        if (!PngReader.TryDecode(data, out var png)) return false;

        var source = MapColorEncoding(png);
        if (!PngHdr.IsSupportedTransfer(source.Transfer)) return false;
        if (png.Cicp is { MatrixCoefficients: not MatrixCoefficients.Identity }) return false;
        // H.273's narrow-range equations start at 8 bits; a 1/2/4-bit grey level was scaled to 0-255 by the
        // reader, which is not a code at any depth the equations define. (Indexed colours are 8-bit palette entries.)
        if (!source.FullRange && png.ColorType == 0 && png.SourceBitDepth < 8) return false;

        long samples = (long)png.Width * png.Height * 4;
        if (samples > Array.MaxLength) return false;

        float[] output;
        try
        {
            output = new float[samples];
        }
        catch (OutOfMemoryException)
        {
            // A Try method does not let an allocation failure escape; the declared size is what the file
            // chose, and a float raster is 16 bytes a pixel against the 4 the file's own RGBA8 would be.
            return false;
        }

        try
        {
            PngFloatFill.Fill(png, source, output);
        }
        catch (InvalidDataException)
        {
            // An indexed pixel past the palette's end, or an indexed image with no palette.
            return false;
        }

        rgba = output;
        width = png.Width;
        height = png.Height;
        encoding = new ColorEncoding
        {
            Primaries = source.Primaries,
            Transfer = TransferFunction.Linear,
            FullRange = true,
            Float = FloatSemantics.DisplayReferred,
        };
        mdcv = png.Mdcv;
        clli = png.Clli;
        return true;
    }

    // The shared first step of the RGBA8 projections: PngReader.TryDecode, then the refusal of cICP
    // PQ/HLG, whose code values are encoded for an HDR EOTF (see TryDecodeIntoRgba8). The float
    // projection (TryDecodeRgbaFloat) is the one that decodes those: it starts from PngReader.TryDecode
    // instead and skips the refusal.
    private static bool TryReadDisplayReferred(ReadOnlySpan<byte> data, [NotNullWhen(true)] out PngImage? png)
    {
        if (!PngReader.TryDecode(data, out png)) return false;
        if (png.Cicp is { } cicp &&
            cicp.TransferFunction is TransferFunction.Pq or TransferFunction.Hlg)
        {
            png = null;
            return false;
        }
        return true;
    }

    // The layout TryDecode produces. Indexed always expands to RGBA8; the rest keep
    // their native channel count and bit depth. Mirrors the ToDecoded mapping below.
    private static bool TryMapLayout(int colorType, int bitDepth, out int channels, out SampleFormat format)
    {
        format = bitDepth == 16 ? SampleFormat.UInt16 : SampleFormat.UInt8;
        switch (colorType)
        {
            case 0: channels = 1; break;                                    // greyscale
            case 2: channels = 3; break;                                    // RGB
            case 3: channels = 4; format = SampleFormat.UInt8; break;       // indexed -> palette-expanded RGBA8
            case 4: channels = 2; break;                                    // greyscale + alpha
            case 6: channels = 4; break;                                    // RGBA
            default: channels = 0; return false;
        }
        return true;
    }

    private static RasterImage ToDecoded(PngImage png)
    {
        var icc = png.IccProfile;
        var color = MapColorEncoding(png);

        // Indexed: palette (+ optional tRNS) expansion to RGBA8 - PngImage.ToRgba8 already does this.
        if (png.ColorType == 3)
            return new RasterImage(png.Width, png.Height, 4, SampleFormat.UInt8, png.ToRgba8(), icc, color);

        var channels = PngReader.SamplesPerPixel(png.ColorType);
        if (png.BitDepth == 16)
        {
            // PngImage.Pixels holds 16-bit samples big-endian; IDecodedImage requires
            // host byte order. AsUInt16Samples returns host-native ushort[].
            var samples = png.AsUInt16Samples();
            var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
            return new RasterImage(png.Width, png.Height, channels, SampleFormat.UInt16, bytes, icc, color);
        }

        // 8-bit: Pixels are already the interleaved host-order raster.
        return new RasterImage(png.Width, png.Height, channels, SampleFormat.UInt8, png.Pixels, icc, color);
    }

    // cICP is authoritative when present (PNG-3 gives it precedence over iCCP /
    // sRGB / gAMA); CicpChunk and ColorEncoding share the H.273 vocabulary, so
    // the mapping is a straight property copy. gAMA 1.0 declares linear-light
    // samples (e.g. astro masters) - H.273 codepoint 8 (Linear) says exactly
    // that. Anything else (sRGB chunk, gAMA ~1/2.2, untagged) lands on the sRGB
    // assumption; the raw gAMA / cHRM values stay available via PngReader for
    // consumers that need them.
    internal static ColorEncoding MapColorEncoding(PngImage png)
    {
        if (png.Cicp is { } cicp)
            return new ColorEncoding
            {
                Primaries = cicp.ColorPrimaries,
                Transfer = cicp.TransferFunction,
                FullRange = cicp.VideoFullRangeFlag,
            };

        if (png.Gamma == 1.0)
            return ColorEncoding.AssumedSrgb with { Transfer = TransferFunction.Linear };

        return ColorEncoding.AssumedSrgb;
    }
}
