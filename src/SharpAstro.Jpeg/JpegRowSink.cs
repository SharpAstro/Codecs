namespace SharpAstro.Jpeg;

/// <summary>
/// Receives the decoder's output one assembled row at a time, as packed 8-bit RGBA.
/// <see cref="JpegCore"/> colour-converts straight into the span <see cref="RowBuffer"/> hands out, so a
/// sink that owns the final destination (<see cref="JpegDestinationSink"/>) costs no copy, and a sink that
/// reduces rows as they arrive (a box filter, a region crop) never needs a full-height buffer.
///
/// <para>Rows arrive in increasing order from 0 and each is <c>width * 4</c> bytes. The engine may have to
/// start over: a baseline stream that carries a further scan after the one it streamed, or a colour marker
/// that only turns up after the scan, cannot be decoded a few rows at a time and falls back to whole planes.
/// Delivery then begins again at row 0, and every row is overwritten. A sink that accumulates across rows
/// must treat row 0 as "reset".</para>
/// </summary>
internal interface IJpegRowSink
{
    /// <summary>Called once, after the frame header and before the first row, with the output size at the decode scale.</summary>
    void Begin(int width, int height);

    /// <summary>The buffer the engine writes output row <paramref name="row"/> into: exactly <c>width * 4</c> bytes.</summary>
    Span<byte> RowBuffer(int row);

    /// <summary>Called after the row's bytes are written to the span <see cref="RowBuffer"/> returned.</summary>
    void RowDone(int row);
}

/// <summary>What a decode held and which engine ran it. Read by tests through <c>InternalsVisibleTo</c>.</summary>
/// <param name="PeakPlaneBytes">Bytes held in sample strips or planes at once: the length of the arrays
/// actually rented or allocated for them (so pool rounding is included), summed over components. Line buffers
/// and progressive coefficient stores are not counted.</param>
/// <param name="WholePlanes">True when the final pass kept whole-image planes (baseline with several scans,
/// or a restart), false when it streamed through strips.</param>
/// <param name="Restarted">True when a streaming attempt had to be abandoned and the stream decoded again.</param>
internal readonly record struct JpegDecodeStats(long PeakPlaneBytes, bool WholePlanes, bool Restarted);

/// <summary>Writes rows into a caller-supplied RGBA buffer, tightly packed.</summary>
internal ref struct JpegDestinationSink : IJpegRowSink
{
    private readonly Span<byte> _destination;
    private int _width, _height, _stride;

    public JpegDestinationSink(Span<byte> destination)
    {
        _destination = destination;
    }

    public readonly int Width => _width;
    public readonly int Height => _height;

    public void Begin(int width, int height)
    {
        JpegSinkLimits.ThrowIfTooLarge(width, height);

        var required = (long)width * height * 4;
        if (_destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination needs {required} bytes for {width}×{height} RGBA, got {_destination.Length}.",
                "rgbaDestination");
        }

        _width = width;
        _height = height;
        _stride = width * 4;
    }

    public Span<byte> RowBuffer(int row) => _destination.Slice(row * _stride, _stride);

    public void RowDone(int row)
    {
    }
}

/// <summary>Allocates the RGBA image in <see cref="Begin"/> and writes rows into it.</summary>
internal ref struct JpegAllocatingSink : IJpegRowSink
{
    private byte[]? _pixels;
    private int _width, _height, _stride;

    public readonly int Width => _width;
    public readonly int Height => _height;

    public readonly byte[] Pixels => _pixels ?? throw new InvalidOperationException("The decode has not begun.");

    public void Begin(int width, int height)
    {
        JpegSinkLimits.ThrowIfTooLarge(width, height);

        // A restart delivers the rows again into the same image, so the allocation is kept.
        _pixels ??= new byte[width * height * 4];
        _width = width;
        _height = height;
        _stride = width * 4;
    }

    public Span<byte> RowBuffer(int row) => _pixels.AsSpan(row * _stride, _stride);

    public void RowDone(int row)
    {
    }
}

internal static class JpegSinkLimits
{
    /// <summary>
    /// A full-size RGBA raster is addressed with <c>int</c> offsets, so it cannot exceed <c>int.MaxValue</c>
    /// bytes. This is the sink's limit rather than the engine's: a sink that reduces rows as they arrive
    /// never holds a raster that size.
    /// </summary>
    public static void ThrowIfTooLarge(int width, int height)
    {
        if ((long)width * height * 4 > int.MaxValue)
            throw new InvalidDataException("JPEG: too large");
    }
}
