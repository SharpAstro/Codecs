namespace SharpAstro.Jpeg;

/// <summary>
/// Pure-managed JPEG (ITU-T T.81) decoder. Baseline sequential and progressive
/// DCT, restart intervals, all chroma subsampling layouts, grayscale, YCbCr,
/// RGB-marked, and Adobe CMYK / YCCK streams. Output is always packed 8-bit RGBA.
///
/// <para>
/// Full-scale decode is byte-exact against the stb_image reference decoder
/// (<c>StbImageSharp</c>). Scaled decode (<see cref="JpegScale.Half"/> and below)
/// runs a reduced inverse DCT so neither the full-resolution sample planes nor
/// the full-resolution output ever exist — the key property for decoding huge
/// scans straight to thumbnail/LOD size without large-object-heap churn. All
/// intermediate buffers are pooled; pair with <see cref="DecodeTo"/> and a
/// caller-pooled destination for allocation-free decoding.
/// </para>
///
/// <para>
/// At any scale the decoder holds a few MCU rows of samples, not whole-image component planes: baseline and
/// progressive streams are inverse-transformed, upsampled and colour-converted a row strip at a time, so the
/// sample memory depends on the image's width and not its height (a progressive stream still keeps its
/// coefficients whole, since every scan is needed). Baseline streams whose scans do not each carry every
/// component fall back to whole planes. The output is byte-identical either way.
/// </para>
/// </summary>
public static class JpegDecoder
{
    /// <summary>
    /// Reads the frame header only (dimensions, component count, progressive
    /// flag) — no entropy decode, no allocation beyond the result.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    public static JpegInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        var core = new JpegCore(data, 1);
        return core.ParseInfo();
    }

    /// <summary>Decodes to a freshly allocated RGBA image at the requested scale.</summary>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    public static JpegImage Decode(ReadOnlySpan<byte> data, JpegScale scale = JpegScale.Full)
    {
        var sink = new JpegAllocatingSink();
        JpegCore.Run(data, ValidateScale(scale), ref sink);
        return new JpegImage(sink.Width, sink.Height, sink.Pixels);
    }

    /// <summary>
    /// Decodes into a caller-supplied buffer (use <see cref="ReadInfo"/> +
    /// <see cref="JpegInfo.ScaledSize"/> to size it: width × height × 4 bytes).
    /// Returns the actual output dimensions. Rows are written tightly packed, as they are decoded:
    /// a stream that turns out to be corrupt part-way leaves the rows before the failure in the buffer
    /// (the size is checked against the frame header first, before anything is written).
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination, JpegScale scale = JpegScale.Full)
    {
        var sink = new JpegDestinationSink(rgbaDestination);
        JpegCore.Run(data, ValidateScale(scale), ref sink);
        return (sink.Width, sink.Height);
    }

    /// <summary>
    /// <see cref="Decode"/> that also reports what the decode held and which engine ran it. Tests read the
    /// strip bound through this. <paramref name="forceWholePlanes"/> runs the whole-plane engine (stb's
    /// pipeline before strips) as the reference the strip pipeline must match byte for byte.
    /// </summary>
    internal static JpegDecodeStats DecodeWithStats(ReadOnlySpan<byte> data, JpegScale scale, out JpegImage image, bool forceWholePlanes = false)
    {
        var sink = new JpegAllocatingSink();
        var stats = JpegCore.Run(data, ValidateScale(scale), ref sink, forceWholePlanes);
        image = new JpegImage(sink.Width, sink.Height, sink.Pixels);
        return stats;
    }

    private static int ValidateScale(JpegScale scale)
    {
        var s = (int)scale;
        if (s != 1 && s != 2 && s != 4 && s != 8)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be Full, Half, Quarter, or Eighth.");
        return s;
    }
}
