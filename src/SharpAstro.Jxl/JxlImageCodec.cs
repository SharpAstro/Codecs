namespace SharpAstro.Jxl;

/// <summary>A decoded lossless JPEG XL image: integer colour channels plus geometry.</summary>
public sealed record JxlImage
{
    /// <summary>Image width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Number of colour channels: 1 (grey) or 3 (RGB).</summary>
    public required int ColorChannels { get; init; }

    /// <summary>Declared integer sample precision (e.g. 8 or 16).</summary>
    public required int BitsPerSample { get; init; }

    /// <summary>Row-major samples, <c>[ColorChannels][Width*Height]</c>.</summary>
    public required int[][] Channels { get; init; }
}

/// <summary>
/// Top-level façade: integer pixels ⟷ a lossless Modular <c>.jxl</c> codestream. Wraps
/// <see cref="JxlModularEncoder"/> / <see cref="JxlModularFrame"/> to produce bare codestreams
/// (the 0xFF 0x0A form, valid per ISO/IEC 18181-2) that libjxl / Magick / browsers decode.
///
/// Scope: still-image lossless Modular — 8/16-bit grey or RGB, single group (each dimension
/// ≤ 1024), no alpha. RGB is decorrelated with the reversible YCoCg-R colour transform. Lossy
/// VarDCT and float/HDR samples are not yet supported (a future rung).
/// </summary>
public static class JxlImageCodec
{
    /// <summary>
    /// Decodes any still-image lossless Modular <c>.jxl</c> (bare codestream or ISOBMFF container;
    /// grey or RGB; 8/16-bit) into integer channels. Throws <see cref="NotSupportedException"/> for
    /// constructs outside the supported scope (VarDCT, multi-group, alpha, float, …).
    /// </summary>
    public static JxlImage Decode(ReadOnlySpan<byte> jxl)
    {
        JxlModularDecodeResult result = JxlModularFrame.Decode(jxl.ToArray());
        return new JxlImage
        {
            Width = result.Width,
            Height = result.Height,
            ColorChannels = result.ColorChannels,
            BitsPerSample = result.BitsPerSample,
            Channels = result.Channels,
        };
    }

    /// <inheritdoc cref="Decode(ReadOnlySpan{byte})"/>
    public static JxlImage Decode(byte[] jxl) => Decode(jxl.AsSpan());

    // ---- 8-bit ----

    /// <summary>
    /// Encodes a <paramref name="width"/>×<paramref name="height"/> 8-bit RGB image — each channel
    /// <c>width*height</c> samples in raster order, values 0..255 — as a lossless <c>.jxl</c>.
    /// </summary>
    public static byte[] EncodeRgb24(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b, int width, int height) =>
        EncodeRgb(r, g, b, width, height, bitsPerSample: 8);

    /// <summary>Decodes a lossless 8-bit RGB <c>.jxl</c> into three channels (values 0..255).</summary>
    public static (int Width, int Height, int[] R, int[] G, int[] B) DecodeRgb24(ReadOnlySpan<byte> jxl) =>
        DecodeRgb(jxl);

    /// <summary>
    /// Encodes a <paramref name="width"/>×<paramref name="height"/> 8-bit grayscale image
    /// (<c>width*height</c> samples in raster order, values 0..255) as a lossless <c>.jxl</c>.
    /// </summary>
    public static byte[] EncodeGray8(ReadOnlySpan<int> y, int width, int height) =>
        EncodeGray(y, width, height, bitsPerSample: 8);

    /// <summary>Decodes a lossless 8-bit grayscale <c>.jxl</c> into one channel (values 0..255).</summary>
    public static (int Width, int Height, int[] Y) DecodeGray8(ReadOnlySpan<byte> jxl) =>
        DecodeGray(jxl);

    // ---- 16-bit ----

    /// <summary>
    /// Encodes a <paramref name="width"/>×<paramref name="height"/> 16-bit RGB image — each channel
    /// <c>width*height</c> samples, raster order, values 0..65535 — as a lossless <c>.jxl</c>.
    /// </summary>
    public static byte[] EncodeRgb48(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b, int width, int height) =>
        EncodeRgb(r, g, b, width, height, bitsPerSample: 16);

    /// <summary>Decodes a lossless 16-bit RGB <c>.jxl</c> into three channels (values 0..65535).</summary>
    public static (int Width, int Height, int[] R, int[] G, int[] B) DecodeRgb48(ReadOnlySpan<byte> jxl) =>
        DecodeRgb(jxl);

    /// <summary>
    /// Encodes a <paramref name="width"/>×<paramref name="height"/> 16-bit grayscale image
    /// (<c>width*height</c> samples, raster order, values 0..65535) as a lossless <c>.jxl</c>.
    /// </summary>
    public static byte[] EncodeGray16(ReadOnlySpan<int> y, int width, int height) =>
        EncodeGray(y, width, height, bitsPerSample: 16);

    /// <summary>Decodes a lossless 16-bit grayscale <c>.jxl</c> into one channel (values 0..65535).</summary>
    public static (int Width, int Height, int[] Y) DecodeGray16(ReadOnlySpan<byte> jxl) =>
        DecodeGray(jxl);

    // ---- cores ----

    private static byte[] EncodeRgb(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b, int width, int height, int bitsPerSample)
    {
        int n = width * height;
        if (r.Length < n || g.Length < n || b.Length < n)
            throw new ArgumentException("Each RGB channel must hold width*height samples.");
        int[][] channels = [r[..n].ToArray(), g[..n].ToArray(), b[..n].ToArray()];
        return JxlModularEncoder.Encode(channels, width, height, bitsPerSample, grayscale: false);
    }

    private static byte[] EncodeGray(ReadOnlySpan<int> y, int width, int height, int bitsPerSample)
    {
        int n = width * height;
        if (y.Length < n)
            throw new ArgumentException("The grayscale channel must hold width*height samples.", nameof(y));
        int[][] channels = [y[..n].ToArray()];
        return JxlModularEncoder.Encode(channels, width, height, bitsPerSample, grayscale: true);
    }

    private static (int Width, int Height, int[] R, int[] G, int[] B) DecodeRgb(ReadOnlySpan<byte> jxl)
    {
        JxlImage img = Decode(jxl);
        if (img.ColorChannels != 3)
            throw new InvalidDataException($"Expected an RGB JPEG XL image, but it has {img.ColorChannels} colour channel(s).");
        return (img.Width, img.Height, img.Channels[0], img.Channels[1], img.Channels[2]);
    }

    private static (int Width, int Height, int[] Y) DecodeGray(ReadOnlySpan<byte> jxl)
    {
        JxlImage img = Decode(jxl);
        if (img.ColorChannels != 1)
            throw new InvalidDataException($"Expected a grayscale JPEG XL image, but it has {img.ColorChannels} colour channel(s).");
        return (img.Width, img.Height, img.Channels[0]);
    }
}
