namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 only. No libwebp, libvpx or
// RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are used only as black-box
// test oracles.

/// <summary>Options for <see cref="WebpEncoder"/>.</summary>
public sealed class WebpEncodeOptions
{
    /// <summary>An ICC profile to store in an <c>ICCP</c> chunk, or null or empty for none.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>An EXIF payload to store in an <c>EXIF</c> chunk, or null or empty for none.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>An XMP packet to store in an <c>XMP </c> chunk, or null or empty for none.</summary>
    public byte[]? Xmp { get; init; }

    /// <summary>
    /// How hard the encoder tries, 0 (fastest) to 9 (smallest output). Accepted so callers can
    /// start passing it; the literal-only encoder has nothing to trade yet and ignores it.
    /// </summary>
    public int Effort { get; init; } = 6;
}

/// <summary>
/// A WebP encoder. Lossless output only so far: VP8L with literal-only prefix codes (no transforms,
/// no backward references, no colour cache), which is valid for every decoder but not yet small.
/// </summary>
public static class WebpEncoder
{
    /// <summary>The largest width or height a VP8L header can carry (14 bits, minus-one coded).</summary>
    public const int MaximumDimension = 16384;

    /// <summary>
    /// Encodes tightly packed or strided, non-premultiplied RGBA8 pixels as a lossless WebP file.
    /// </summary>
    /// <param name="rgba">The pixels, four bytes (R, G, B, A) each, row by row.</param>
    /// <param name="width">The width in pixels, 1 to 16384.</param>
    /// <param name="height">The height in pixels, 1 to 16384.</param>
    /// <param name="stride">The bytes from one row's start to the next's, or 0 for <c>width * 4</c>.</param>
    /// <param name="options">Metadata to attach and the effort, or null for none.</param>
    /// <returns>The WebP file. It is the simple <c>VP8L</c> layout unless metadata is attached.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside 1 to 16384, the pixel count exceeds the decoder's limit, or the effort is outside 0 to 9.</exception>
    /// <exception cref="ArgumentException">The stride is too small or <paramref name="rgba"/> is shorter than the image needs.</exception>
    public static byte[] EncodeLossless(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int stride = 0,
        WebpEncodeOptions? options = null) =>
        Encode(rgba, width, height, stride, 4, options);

    /// <summary>Encodes RGB8 pixels (three bytes each, R, G, B) as an opaque lossless WebP file.</summary>
    /// <param name="rgb">The pixels, row by row.</param>
    /// <param name="width">The width in pixels, 1 to 16384.</param>
    /// <param name="height">The height in pixels, 1 to 16384.</param>
    /// <param name="stride">The bytes from one row's start to the next's, or 0 for <c>width * 3</c>.</param>
    /// <param name="options">Metadata to attach and the effort, or null for none.</param>
    /// <returns>The WebP file. It is the simple <c>VP8L</c> layout unless metadata is attached.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside 1 to 16384, the pixel count exceeds the decoder's limit, or the effort is outside 0 to 9.</exception>
    /// <exception cref="ArgumentException">The stride is too small or <paramref name="rgb"/> is shorter than the image needs.</exception>
    public static byte[] EncodeLosslessRgb(
        ReadOnlySpan<byte> rgb,
        int width,
        int height,
        int stride = 0,
        WebpEncodeOptions? options = null) =>
        Encode(rgb, width, height, stride, 3, options);

    /// <summary>
    /// Encodes Gray8 pixels (one byte each) as an opaque lossless WebP file. WebP has no grey
    /// format, so it stores them as R = G = B and decodes back as such.
    /// </summary>
    /// <param name="gray">The pixels, row by row.</param>
    /// <param name="width">The width in pixels, 1 to 16384.</param>
    /// <param name="height">The height in pixels, 1 to 16384.</param>
    /// <param name="stride">The bytes from one row's start to the next's, or 0 for <c>width</c>.</param>
    /// <param name="options">Metadata to attach and the effort, or null for none.</param>
    /// <returns>The WebP file. It is the simple <c>VP8L</c> layout unless metadata is attached.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside 1 to 16384, the pixel count exceeds the decoder's limit, or the effort is outside 0 to 9.</exception>
    /// <exception cref="ArgumentException">The stride is too small or <paramref name="gray"/> is shorter than the image needs.</exception>
    public static byte[] EncodeLosslessGray(
        ReadOnlySpan<byte> gray,
        int width,
        int height,
        int stride = 0,
        WebpEncodeOptions? options = null) =>
        Encode(gray, width, height, stride, 1, options);

    private static byte[] Encode(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride,
        int bytesPerPixel,
        WebpEncodeOptions? options)
    {
        // Everything is checked before anything is allocated: the dimensions the header can hold,
        // the decoder's pixel limit (the working memory is four bytes per pixel on top of the
        // output, so the same limit bounds it), and the size of the buffer handed in.
        if (width is < 1 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, $"The width must be 1 to {MaximumDimension}.");
        }

        if (height is < 1 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, $"The height must be 1 to {MaximumDimension}.");
        }

        if ((long)width * height > WebpLimits.MaximumPixels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"{width}x{height} is {(long)width * height} pixels, over the limit of {WebpLimits.MaximumPixels}.");
        }

        int effort = options?.Effort ?? 6;
        if (effort is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(options), effort, "The effort must be 0 to 9.");
        }

        long rowBytes = (long)width * bytesPerPixel;
        if (stride == 0)
        {
            stride = (int)rowBytes;
        }
        else if (stride < rowBytes)
        {
            throw new ArgumentException($"The stride {stride} is smaller than a row ({rowBytes} bytes).", nameof(stride));
        }

        long needed = (long)stride * (height - 1) + rowBytes;
        if (source.Length < needed)
        {
            throw new ArgumentException($"The buffer holds {source.Length} bytes but the image needs {needed}.", nameof(source));
        }

        int[] argb = new int[width * height];
        bool alphaIsUsed = false;
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = source.Slice(y * stride, (int)rowBytes);
            switch (bytesPerPixel)
            {
                case 4:
                    for (int x = 0; x < row.Length; x += 4)
                    {
                        byte a = row[x + 3];
                        alphaIsUsed |= a != 255;
                        argb[index++] = (a << 24) | (row[x] << 16) | (row[x + 1] << 8) | row[x + 2];
                    }

                    break;
                case 3:
                    for (int x = 0; x < row.Length; x += 3)
                    {
                        argb[index++] = unchecked((int)0xff000000) | (row[x] << 16) | (row[x + 1] << 8) | row[x + 2];
                    }

                    break;
                default:
                    for (int x = 0; x < row.Length; x++)
                    {
                        int g = row[x];
                        argb[index++] = unchecked((int)0xff000000) | (g << 16) | (g << 8) | g;
                    }

                    break;
            }
        }

        byte[] vp8l = Vp8LEncoder.EncodeLiteral(argb, width, height, alphaIsUsed);
        return WebpWriter.Write(
            vp8l,
            width,
            height,
            alphaIsUsed,
            options?.IccProfile,
            options?.Exif,
            options?.Xmp);
    }
}
