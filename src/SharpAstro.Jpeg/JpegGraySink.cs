using System.Buffers;

namespace SharpAstro.Jpeg;

/// <summary>
/// Writes one byte a pixel into a caller-supplied buffer, tightly packed at <c>width</c> bytes a row. The engine
/// still colour-converts each row to RGBA, into one row of scratch this sink rents, and <see cref="RowDone"/>
/// reduces it: the full-size RGBA, four times the result, is never made.
/// <para>The byte is BT.601 luma, <c>(77 R + 150 G + 29 B + 128) &gt;&gt; 8</c>. The weights sum to 256, so for a
/// single-component stream, whose decoded R, G and B are all its sample, the byte IS that sample.</para>
/// </summary>
internal ref struct JpegGraySink : IJpegRowSink
{
    private readonly Span<byte> _destination;
    private byte[]? _row;
    private int _width, _height;

    public JpegGraySink(Span<byte> destination)
    {
        _destination = destination;
    }

    public readonly int Width => _width;
    public readonly int Height => _height;

    public void Begin(int width, int height)
    {
        JpegSinkLimits.ThrowIfTooLarge(width, height);

        var required = (long)width * height;
        if (_destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination needs {required} bytes for {width}×{height} grey, got {_destination.Length}.",
                "grayDestination");
        }

        _width = width;
        _height = height;
        _row ??= ArrayPool<byte>.Shared.Rent(width * 4);
    }

    public readonly Span<byte> RowBuffer(int row) => _row.AsSpan(0, _width * 4);

    public readonly void RowDone(int row)
    {
        ReadOnlySpan<byte> rgba = _row.AsSpan(0, _width * 4);
        var gray = _destination.Slice(row * _width, _width);
        for (var x = 0; x < gray.Length; x++)
        {
            var i = x * 4;
            gray[x] = (byte)((77 * rgba[i] + 150 * rgba[i + 1] + 29 * rgba[i + 2] + 128) >> 8);
        }
    }

    /// <summary>Returns the row scratch to the pool; call once, after the decode.</summary>
    public void Release()
    {
        if (_row is { } row)
            ArrayPool<byte>.Shared.Return(row);
        _row = null;
    }
}
