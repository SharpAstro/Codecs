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
        var core = new JpegCore(data, ValidateScale(scale));
        try
        {
            core.LoadImage();
            var pixels = new byte[core.OutX * core.OutY * 4];
            core.AssembleRgba(pixels);
            return new JpegImage(core.OutX, core.OutY, pixels);
        }
        finally
        {
            core.ReturnBuffers();
        }
    }

    /// <summary>
    /// Decodes into a caller-supplied buffer (use <see cref="ReadInfo"/> +
    /// <see cref="JpegInfo.ScaledSize"/> to size it: width × height × 4 bytes).
    /// Returns the actual output dimensions. Rows are written tightly packed.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination, JpegScale scale = JpegScale.Full)
    {
        var core = new JpegCore(data, ValidateScale(scale));
        try
        {
            core.LoadImage();
            var required = (long)core.OutX * core.OutY * 4;
            if (rgbaDestination.Length < required)
            {
                throw new ArgumentException(
                    $"Destination needs {required} bytes for {core.OutX}×{core.OutY} RGBA, got {rgbaDestination.Length}.",
                    nameof(rgbaDestination));
            }

            core.AssembleRgba(rgbaDestination);
            return (core.OutX, core.OutY);
        }
        finally
        {
            core.ReturnBuffers();
        }
    }

    private static int ValidateScale(JpegScale scale)
    {
        var s = (int)scale;
        if (s != 1 && s != 2 && s != 4 && s != 8)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be Full, Half, Quarter, or Eighth.");
        return s;
    }
}
