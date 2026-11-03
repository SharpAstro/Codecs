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
    /// Decodes one rectangle of the image into a caller-supplied RGBA buffer, tightly packed at
    /// <c>region.Width * 4</c> bytes per row, and returns the region's size. For a viewer that shows part of a large
    /// scan this is the entry point that does not pay for the rest of it.
    ///
    /// <para><b>Exactness.</b> The bytes are the same crop of <see cref="Decode"/>'s output at the same
    /// <paramref name="scale"/>, byte for byte, for every region and every scale, including regions whose edges fall
    /// inside an MCU or inside the neighbourhood a chroma upsampler blends (the decoder reads the extra rows and
    /// columns it needs and crops them away). <paramref name="region"/> is in output pixels at <paramref name="scale"/>:
    /// at <see cref="JpegScale.Half"/> it indexes the half-size image, whose size
    /// <see cref="JpegInfo.ScaledSize"/> gives.</para>
    ///
    /// <para><b>Cost</b> scales with the region, as far as the stream's structure allows. Sample memory is the
    /// region's width and the MCU row, never the image's.</para>
    /// <list type="bullet">
    /// <item><b>Baseline with restart markers (DRI):</b> entropy decoding starts at the restart interval holding the
    /// region's first MCU row and stops after its last, found by counting the RSTn markers (which must be in sequence,
    /// else the decode starts from the top, as without them). IDCT and colour conversion cover only the region.</item>
    /// <item><b>Baseline without them:</b> entropy decoding runs from the top of the scan through the region's last MCU
    /// row, then stops; rows above the region are decoded but not transformed or assembled.</item>
    /// <item><b>Progressive:</b> every scan is entropy-decoded (each one refines the whole image), but the IDCT and
    /// assembly cover only the region. The coefficients are held whole, as for <see cref="Decode"/>.</item>
    /// <item><b>Baseline with a scan per component:</b> decoded whole, then cropped.</item>
    /// </list>
    /// <para>Entropy data outside what the region needs is not read, so damage there is not reported, where
    /// <see cref="Decode"/> would throw or salvage it. Data after the last needed MCU row is not read at all, which includes
    /// any marker that follows the scan.</para>
    /// </summary>
    /// <param name="data">The JPEG stream.</param>
    /// <param name="region">The pixels wanted, in output pixels at <paramref name="scale"/>.</param>
    /// <param name="rgbaDestination">Receives <c>region.Width * region.Height * 4</c> bytes; nothing past that is written.</param>
    /// <param name="scale">The decode scale; <paramref name="region"/> is in its coordinates.</param>
    /// <returns>The region's size: <c>(region.Width, region.Height)</c>.</returns>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    /// <exception cref="ArgumentException">The region is empty, or not entirely inside the image at this scale, or the
    /// destination is too small. Raised from the frame header, before any entropy data is read.</exception>
    public static (int Width, int Height) DecodeRegionTo(ReadOnlySpan<byte> data, JpegRegion region, Span<byte> rgbaDestination, JpegScale scale = JpegScale.Full)
    {
        var sink = new JpegRegionSink(rgbaDestination, region);
        JpegCore.Run(data, ValidateScale(scale), ref sink, region: region);
        return (region.Width, region.Height);
    }

    /// <summary>
    /// <see cref="DecodeRegionTo"/> that also reports what the decode held and which engine ran it, as
    /// <see cref="DecodeWithStats"/> does for a full decode. Tests read the region's cost through this.
    /// </summary>
    internal static JpegDecodeStats DecodeRegionToWithStats(ReadOnlySpan<byte> data, JpegRegion region, Span<byte> rgbaDestination, JpegScale scale = JpegScale.Full, bool forceWholePlanes = false)
    {
        var sink = new JpegRegionSink(rgbaDestination, region);
        return JpegCore.Run(data, ValidateScale(scale), ref sink, forceWholePlanes, region);
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
