using System.Buffers;

namespace SharpAstro.Jpeg;

/// <summary>
/// Box-filters the rows it is handed down to a target size as they arrive, so the full-resolution RGBA and any
/// full-height intermediate never exist: the engine colour-converts one source row into a scratch row, the sink adds
/// it into the accumulator of the target row it belongs to, and when a target row's last source row has arrived it is
/// written to the destination and the accumulator starts over. Memory held is one source row, one accumulator of
/// <c>targetWidth * 4</c> sums and the column boxes, whatever the image's height.
///
/// <para><b>The arithmetic is the viewer's <c>RgbaBoxDownsample.Shrink</c>, not an approximation of it.</b> For a
/// source <c>w × h</c> and a target <c>newW × newH</c> the box of target column <c>dx</c> is
/// <c>[dx*w/newW, (dx+1)*w/newW)</c> (integer division), the same for rows; R, G, B and A are summed over the box and
/// the result is <c>(sum + n/2) / n</c> with <c>n = rows * columns</c>. Shrink writes the upper bound as
/// <c>min(w, max(lower + 1, (dx+1)*w/newW))</c>; with <c>newW &lt;= w</c> that is exactly <c>(dx+1)*w/newW</c>, because
/// <c>floor(x + w/newW) &gt;= floor(x) + 1</c> when <c>w/newW &gt;= 1</c> and <c>(dx+1)*w/newW &lt;= w</c> by
/// construction. So the boxes partition the source (each lower bound is the previous upper bound), every source pixel
/// falls in exactly one box, and a row converted once and added once gives the result of converting the whole image and
/// then filtering it. That is why a target larger than the source is refused: Shrink grows a box to one pixel there and
/// the boxes would overlap, and this sink would no longer match it.</para>
///
/// <para>Where this differs from Shrink is only where Shrink is not defined: it sums in <c>int</c>, which wraps once a
/// box holds more than 2^31/255 (about 8.4 million) source pixels, e.g. a 5100×6600 scan fitted to 1×1. The sums here
/// are <c>long</c>, so that corner is the correct mean instead of a wrapped one; everything Shrink computes without
/// overflow is bit for bit the same.</para>
///
/// <para><b>Source rows</b> are the region's rows when a region is given (the engine asks this sink for the image's own
/// row numbers, starting at the region's first), else the whole image's. A restart delivers them again from the first
/// row, and the sink begins its target rows over then, so nothing accumulates across the two attempts.</para>
/// </summary>
internal ref struct JpegFittedSink : IJpegRowSink
{
    private readonly Span<byte> _destination;
    private readonly int _targetWidth, _targetHeight;
    private readonly JpegRegion? _region;

    private int _sourceWidth, _sourceHeight;
    private int _firstRow;                 // the image row the first source row is: the region's Y, or 0
    private int[]? _columnBounds;          // pooled: [dx] = dx * sourceWidth / targetWidth, targetWidth + 1 of them
    private byte[]? _rowScratch;           // pooled: one source row, which is what the engine converts into
    private long[]? _accumulator;          // pooled: targetWidth * 4 sums for the target row under way
    private int _targetRow;                // the target row being accumulated
    private int _boxStart, _boxEnd;        // its source rows, counted from the first: [start, end)

    public JpegFittedSink(Span<byte> destination, int targetWidth, int targetHeight, JpegRegion? region)
    {
        _destination = destination;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _region = region;
    }

    /// <summary>Called once the frame header is read and any region is checked, before a row is decoded.</summary>
    public void Begin(int width, int height)
    {
        // The region is already known to lie inside the image (the engine checked it), so its size is the source's.
        var (sourceWidth, sourceHeight) = _region is { } r ? (r.Width, r.Height) : (width, height);
        if (_targetWidth > sourceWidth)
        {
            throw new ArgumentException(
                $"The target is {_targetWidth} wide but the source is only {sourceWidth}: a fitted decode only shrinks.", "targetWidth");
        }

        if (_targetHeight > sourceHeight)
        {
            throw new ArgumentException(
                $"The target is {_targetHeight} high but the source is only {sourceHeight}: a fitted decode only shrinks.", "targetHeight");
        }

        // The target is no larger than a source of at most 2^16 pixels a side, so the product cannot overflow a long.
        var required = (long)_targetWidth * _targetHeight * 4;
        if (_destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination needs {required} bytes for {_targetWidth}×{_targetHeight} RGBA, got {_destination.Length}.",
                "rgbaDestination");
        }

        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _firstRow = _region?.Y ?? 0;

        _columnBounds = ArrayPool<int>.Shared.Rent(_targetWidth + 1);
        for (var dx = 0; dx <= _targetWidth; ++dx)
            _columnBounds[dx] = (int)((long)dx * sourceWidth / _targetWidth);

        _rowScratch = ArrayPool<byte>.Shared.Rent(sourceWidth * 4);
        _accumulator = ArrayPool<long>.Shared.Rent(_targetWidth * 4);
        StartOver();
    }

    public Span<byte> RowBuffer(int row) => _rowScratch.AsSpan(0, _sourceWidth * 4);

    public void RowDone(int row)
    {
        var sourceRow = row - _firstRow;

        // The first row again means the engine restarted (see IJpegRowSink): nothing from the first attempt is kept,
        // and the destination rows it wrote are overwritten as the new attempt reaches them.
        if (sourceRow == 0)
            StartOver();

        Accumulate();

        if (sourceRow + 1 == _boxEnd)
            WriteTargetRow();
    }

    /// <summary>Returns the pooled buffers. Safe to call more than once, and after a <see cref="Begin"/> that threw.</summary>
    public void Release()
    {
        if (_columnBounds is { } bounds)
            ArrayPool<int>.Shared.Return(bounds);
        if (_rowScratch is { } scratch)
            ArrayPool<byte>.Shared.Return(scratch);
        if (_accumulator is { } sums)
            ArrayPool<long>.Shared.Return(sums);

        _columnBounds = null;
        _rowScratch = null;
        _accumulator = null;
    }

    private void StartOver()
    {
        _accumulator.AsSpan(0, _targetWidth * 4).Clear();
        _targetRow = 0;
        _boxStart = 0;
        _boxEnd = RowEnd(0);
    }

    /// <summary>One past the last source row (from the first) of target row <paramref name="targetRow"/>.</summary>
    private readonly int RowEnd(int targetRow) => (int)((long)(targetRow + 1) * _sourceHeight / _targetHeight);

    /// <summary>Adds the source row just converted into the sums of every target column.</summary>
    private readonly void Accumulate()
    {
        var row = _rowScratch.AsSpan(0, _sourceWidth * 4);
        var bounds = _columnBounds.AsSpan(0, _targetWidth + 1);
        var sums = _accumulator.AsSpan(0, _targetWidth * 4);

        for (var dx = 0; dx < _targetWidth; ++dx)
        {
            // A box's row sum fits an int (at most 255 * 65535), and the sums across rows are long.
            int r = 0, g = 0, b = 0, a = 0;
            var end = bounds[dx + 1] * 4;
            for (var i = bounds[dx] * 4; i < end; i += 4)
            {
                r += row[i];
                g += row[i + 1];
                b += row[i + 2];
                a += row[i + 3];
            }

            var s = dx * 4;
            sums[s] += r;
            sums[s + 1] += g;
            sums[s + 2] += b;
            sums[s + 3] += a;
        }
    }

    /// <summary>The target row's source rows have all arrived: average, write, and move on to the next one.</summary>
    private void WriteTargetRow()
    {
        var bounds = _columnBounds.AsSpan(0, _targetWidth + 1);
        var sums = _accumulator.AsSpan(0, _targetWidth * 4);
        var rowsInBox = _boxEnd - _boxStart;
        var dst = _destination.Slice(_targetRow * _targetWidth * 4, _targetWidth * 4);

        for (var dx = 0; dx < _targetWidth; ++dx)
        {
            var n = (long)rowsInBox * (bounds[dx + 1] - bounds[dx]);
            var half = n / 2;
            var s = dx * 4;
            for (var c = 0; c < 4; ++c)
            {
                dst[s + c] = (byte)((sums[s + c] + half) / n);
                sums[s + c] = 0;
            }
        }

        ++_targetRow;
        _boxStart = _boxEnd;
        // Past the last target row nothing more arrives; the sentinel only has to never be reached.
        _boxEnd = _targetRow < _targetHeight ? RowEnd(_targetRow) : int.MaxValue;
    }
}
