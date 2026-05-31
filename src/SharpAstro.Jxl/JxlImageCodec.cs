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

    /// <summary>Declared sample precision in bits (e.g. 8/16 integer, or 16/32 for float).</summary>
    public required int BitsPerSample { get; init; }

    /// <summary>True when the samples are IEEE floats stored as their bit pattern (see <see cref="Channels"/>).</summary>
    public required bool FloatingPoint { get; init; }

    /// <summary>Exponent-bit count for float samples (8 for F32, 5 for F16); 0 for integer.</summary>
    public required int ExponentBits { get; init; }

    /// <summary>
    /// Row-major samples, <c>[ColorChannels][Width*Height]</c>. For integer images these are the
    /// pixel values; for float images each entry is the IEEE bit pattern reinterpreted as a signed
    /// integer (F32 = 32-bit pattern, F16 = the 16-bit half pattern sign-extended).
    /// </summary>
    public required int[][] Channels { get; init; }
}

/// <summary>
/// Top-level façade: pixels ⟷ a <c>.jxl</c> codestream (the 0xFF 0x0A form, valid per
/// ISO/IEC 18181-2; decoded by libjxl / Magick / browsers).
///
/// <para><b>Lossless Modular</b> (<see cref="JxlModularEncoder"/> / <see cref="JxlModularFrame"/>):
/// 8/16-bit integer or IEEE-float (F16/F32) grey or RGB, single group (each dimension ≤ 1024), no
/// alpha; RGB is decorrelated with the reversible YCoCg-R colour transform; float samples are stored
/// verbatim (HDR-safe, not normalised).</para>
///
/// <para><b>Lossy VarDCT</b> (<see cref="JxlVarDctEncoder"/> / <see cref="JxlVarDctFrame"/>) via
/// <see cref="EncodeRgb24Lossy"/>: 8-bit RGB at a fixed high-quality setting (DCT8, XYB colour,
/// full-resolution chroma), dimensions multiples of 8 and ≤ 16384 px. <see cref="Decode"/>
/// auto-detects the encoding and handles both. A tunable quality/distance knob, grayscale-lossy and
/// arbitrary lossy dimensions remain follow-ups.</para>
/// </summary>
public static class JxlImageCodec
{
    /// <summary>
    /// Decodes a <c>.jxl</c> (bare codestream or ISOBMFF container) into channels, dispatching on the
    /// frame encoding: lossless Modular (grey/RGB, 8/16-bit integer or F16/F32 float) or our lossy
    /// VarDCT output (returned as 8-bit RGB). Throws <see cref="NotSupportedException"/> for constructs
    /// outside the supported scope (multi-group Modular, alpha, …).
    /// </summary>
    public static JxlImage Decode(ReadOnlySpan<byte> jxl)
    {
        byte[] bytes = jxl.ToArray();
        if (PeekEncoding(bytes, out int vw, out int vh) == JxlFrameEncoding.VarDct)
        {
            // Lossy VarDCT (our DCT8 path): reconstructs to 8-bit integer RGB.
            int[][] rgb = JxlVarDctFrame.DecodeToRgb24(bytes);
            return new JxlImage
            {
                Width = vw,
                Height = vh,
                ColorChannels = 3,
                BitsPerSample = 8,
                FloatingPoint = false,
                ExponentBits = 0,
                Channels = rgb,
            };
        }

        JxlModularDecodeResult result = JxlModularFrame.Decode(bytes);
        return new JxlImage
        {
            Width = result.Width,
            Height = result.Height,
            ColorChannels = result.ColorChannels,
            BitsPerSample = result.BitsPerSample,
            FloatingPoint = result.FloatingPoint,
            ExponentBits = result.ExponentBits,
            Channels = result.Channels,
        };
    }

    /// <inheritdoc cref="Decode(ReadOnlySpan{byte})"/>
    public static JxlImage Decode(byte[] jxl) => Decode(jxl.AsSpan());

    /// <summary>Reads just the headers to learn the frame encoding (+ dimensions) so <see cref="Decode"/> can dispatch.</summary>
    private static JxlFrameEncoding PeekEncoding(byte[] containerOrCodestream, out int width, out int height)
    {
        byte[] cs = JxlContainer.ExtractCodestream(containerOrCodestream);
        var br = new JxlBitReader(cs.AsSpan(2)); // skip FF 0A
        (width, height) = JxlSizeHeader.Read(ref br);
        JxlImageMetadata meta = JxlImageMetadata.Read(ref br);
        JxlFrameHeader frame = JxlFrameHeader.Read(ref br, meta, width, height);
        return frame.Encoding;
    }

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
    /// Encodes a <paramref name="width"/>×<paramref name="height"/> 8-bit RGB image (each channel
    /// <c>width*height</c> samples, raster order, values 0..255) as a <b>lossy</b> VarDCT <c>.jxl</c>
    /// at a fixed high-quality setting (DCT8, XYB colour, full-resolution chroma).
    /// <paramref name="width"/>/<paramref name="height"/> must be multiples of 8 and ≤ 16384.
    /// Decode with <see cref="Decode(ReadOnlySpan{byte})"/>, which auto-detects the VarDCT encoding.
    /// </summary>
    public static byte[] EncodeRgb24Lossy(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b, int width, int height)
    {
        int n = width * height;
        if (r.Length < n || g.Length < n || b.Length < n)
            throw new ArgumentException("Each RGB channel must hold width*height samples.");
        int[][] rgb = [r[..n].ToArray(), g[..n].ToArray(), b[..n].ToArray()];
        return JxlVarDctEncoder.EncodeRgb24(rgb, width, height);
    }

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

    // ---- IEEE float / HDR ----
    //
    // JPEG XL stores float samples as their IEEE bit pattern reinterpreted as a signed integer in a
    // Modular channel (no zigzag remap): F32 = the 32-bit pattern as i32, F16 = the 16-bit half
    // pattern as i16. These façade methods do that reinterpretation around the integer Modular
    // codec — the codec itself is bit-exact, so float values round-trip exactly (lossless).

    private static readonly JxlBitDepth F32Depth = new() { FloatingPoint = true, BitsPerSample = 32, ExponentBits = 8 };
    private static readonly JxlBitDepth F16Depth = new() { FloatingPoint = true, BitsPerSample = 16, ExponentBits = 5 };

    /// <summary>Encodes a 32-bit-float grayscale image (values stored verbatim, not normalised) as a lossless <c>.jxl</c>.</summary>
    public static byte[] EncodeGrayF32(ReadOnlySpan<float> y, int width, int height)
    {
        int[] bits = F32ToBits(y, width * height);
        return JxlModularEncoder.Encode([bits], width, height, F32Depth, grayscale: true);
    }

    /// <summary>Decodes a lossless 32-bit-float grayscale <c>.jxl</c> into one channel.</summary>
    public static (int Width, int Height, float[] Y) DecodeGrayF32(ReadOnlySpan<byte> jxl)
    {
        JxlImage img = DecodeFloat(jxl, channels: 1, bits: 32);
        return (img.Width, img.Height, BitsToF32(img.Channels[0]));
    }

    /// <summary>Encodes a 16-bit half-float grayscale image as a lossless <c>.jxl</c>.</summary>
    public static byte[] EncodeGrayF16(ReadOnlySpan<Half> y, int width, int height)
    {
        int[] bits = F16ToBits(y, width * height);
        return JxlModularEncoder.Encode([bits], width, height, F16Depth, grayscale: true);
    }

    /// <summary>Decodes a lossless 16-bit half-float grayscale <c>.jxl</c> into one channel.</summary>
    public static (int Width, int Height, Half[] Y) DecodeGrayF16(ReadOnlySpan<byte> jxl)
    {
        JxlImage img = DecodeFloat(jxl, channels: 1, bits: 16);
        return (img.Width, img.Height, BitsToF16(img.Channels[0]));
    }

    /// <summary>
    /// Encodes a 16-bit half-float RGB image from an <b>interleaved</b> <c>Half[width*height*3]</c>
    /// (RGBRGB…) — the consumer's HDR RGB shape — as a lossless <c>.jxl</c>.
    /// </summary>
    public static byte[] EncodeRgbF16(ReadOnlySpan<Half> rgb, int width, int height)
    {
        int n = width * height;
        if (rgb.Length < n * 3)
            throw new ArgumentException("Interleaved RGB half buffer must hold width*height*3 samples.", nameof(rgb));
        int[] r = new int[n], g = new int[n], b = new int[n];
        for (int i = 0; i < n; i++) { r[i] = HalfBits(rgb[i * 3]); g[i] = HalfBits(rgb[i * 3 + 1]); b[i] = HalfBits(rgb[i * 3 + 2]); }
        return JxlModularEncoder.Encode([r, g, b], width, height, F16Depth, grayscale: false);
    }

    /// <summary>Decodes a lossless 16-bit half-float RGB <c>.jxl</c> into an interleaved <c>Half[width*height*3]</c> buffer.</summary>
    public static (int Width, int Height, Half[] Rgb) DecodeRgbF16(ReadOnlySpan<byte> jxl)
    {
        JxlImage img = DecodeFloat(jxl, channels: 3, bits: 16);
        int n = img.Width * img.Height;
        var rgb = new Half[n * 3];
        for (int i = 0; i < n; i++)
        {
            rgb[i * 3] = HalfFromBits(img.Channels[0][i]);
            rgb[i * 3 + 1] = HalfFromBits(img.Channels[1][i]);
            rgb[i * 3 + 2] = HalfFromBits(img.Channels[2][i]);
        }
        return (img.Width, img.Height, rgb);
    }

    /// <summary>
    /// Encodes a 32-bit-float RGB image from an <b>interleaved</b> <c>float[width*height*3]</c>
    /// (RGBRGB…), values verbatim, as a lossless <c>.jxl</c>.
    /// </summary>
    public static byte[] EncodeRgbF32(ReadOnlySpan<float> rgb, int width, int height)
    {
        int n = width * height;
        if (rgb.Length < n * 3)
            throw new ArgumentException("Interleaved RGB float buffer must hold width*height*3 samples.", nameof(rgb));
        int[] r = new int[n], g = new int[n], b = new int[n];
        for (int i = 0; i < n; i++) { r[i] = SingleBits(rgb[i * 3]); g[i] = SingleBits(rgb[i * 3 + 1]); b[i] = SingleBits(rgb[i * 3 + 2]); }
        return JxlModularEncoder.Encode([r, g, b], width, height, F32Depth, grayscale: false);
    }

    /// <summary>Decodes a lossless 32-bit-float RGB <c>.jxl</c> into an interleaved <c>float[width*height*3]</c> buffer.</summary>
    public static (int Width, int Height, float[] Rgb) DecodeRgbF32(ReadOnlySpan<byte> jxl)
    {
        JxlImage img = DecodeFloat(jxl, channels: 3, bits: 32);
        int n = img.Width * img.Height;
        var rgb = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            rgb[i * 3] = SingleFromBits(img.Channels[0][i]);
            rgb[i * 3 + 1] = SingleFromBits(img.Channels[1][i]);
            rgb[i * 3 + 2] = SingleFromBits(img.Channels[2][i]);
        }
        return (img.Width, img.Height, rgb);
    }

    private static JxlImage DecodeFloat(ReadOnlySpan<byte> jxl, int channels, int bits)
    {
        JxlImage img = Decode(jxl);
        if (!img.FloatingPoint || img.BitsPerSample != bits)
            throw new InvalidDataException($"Expected a {bits}-bit float JPEG XL image (float={img.FloatingPoint}, bits={img.BitsPerSample}).");
        if (img.ColorChannels != channels)
            throw new InvalidDataException($"Expected {channels} colour channel(s), but the image has {img.ColorChannels}.");
        return img;
    }

    // IEEE reinterpretation. F16 patterns are stored sign-extended so they fit a 16-bit sample buffer.
    private static int SingleBits(float f) => unchecked((int)BitConverter.SingleToUInt32Bits(f));
    private static float SingleFromBits(int sample) => BitConverter.UInt32BitsToSingle(unchecked((uint)sample));
    private static int HalfBits(Half h) => (short)BitConverter.HalfToUInt16Bits(h);
    private static Half HalfFromBits(int sample) => BitConverter.UInt16BitsToHalf(unchecked((ushort)sample));

    private static int[] F32ToBits(ReadOnlySpan<float> values, int n)
    {
        if (values.Length < n) throw new ArgumentException("Channel must hold width*height samples.");
        var bits = new int[n];
        for (int i = 0; i < n; i++) bits[i] = SingleBits(values[i]);
        return bits;
    }

    private static float[] BitsToF32(int[] samples)
    {
        var values = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++) values[i] = SingleFromBits(samples[i]);
        return values;
    }

    private static int[] F16ToBits(ReadOnlySpan<Half> values, int n)
    {
        if (values.Length < n) throw new ArgumentException("Channel must hold width*height samples.");
        var bits = new int[n];
        for (int i = 0; i < n; i++) bits[i] = HalfBits(values[i]);
        return bits;
    }

    private static Half[] BitsToF16(int[] samples)
    {
        var values = new Half[samples.Length];
        for (int i = 0; i < samples.Length; i++) values[i] = HalfFromBits(samples[i]);
        return values;
    }
}
