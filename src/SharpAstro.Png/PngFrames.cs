namespace SharpAstro.Png;

/// <summary>
/// One composited frame of an animated PNG: the whole canvas as RGBA8 after the frame was drawn, with the time the
/// frame is shown for.
/// </summary>
/// <remarks>
/// The pixels are straight (non-premultiplied) RGBA8, row-major, top-down, tightly packed, expanded from the file's
/// own colour type through <see cref="PngImage.ToRgba8"/>'s mapping (palette and <c>tRNS</c> lookup, 16-bit samples
/// truncated to their high byte). A frame yielded by <see cref="PngReader.EnumerateFrames"/> shares the enumerator's
/// one working canvas: <see cref="Rgba"/> is only valid until the enumerator is advanced, so copy it to keep it.
/// Frames returned by <see cref="PngReader.DecodeFrames(ReadOnlySpan{byte})"/> own their pixels.
/// </remarks>
/// <param name="Index">The frame's zero-based index in play order.</param>
/// <param name="Rgba">The canvas after this frame was drawn: <c>CanvasWidth * CanvasHeight * 4</c> bytes.</param>
/// <param name="DelayNumerator">Numerator of the frame's display time, in seconds (<c>fcTL.delay_num</c>).</param>
/// <param name="DelayDenominator">Denominator of the display time as stored (<c>fcTL.delay_den</c>); 0 stands for 100, see <see cref="DelaySeconds"/>.</param>
public sealed record PngFrame(int Index, byte[] Rgba, ushort DelayNumerator, ushort DelayDenominator)
{
    /// <summary>The frame's display time in seconds, with the specification's rule that a zero denominator means 100.</summary>
    public double DelaySeconds => DelayNumerator / (double)(DelayDenominator == 0 ? 100 : DelayDenominator);
}

/// <summary>An animated PNG with every frame composited and materialised: see <see cref="PngReader.DecodeFrames(ReadOnlySpan{byte})"/>.</summary>
/// <param name="CanvasWidth">The canvas width in pixels (the IHDR width).</param>
/// <param name="CanvasHeight">The canvas height in pixels (the IHDR height).</param>
/// <param name="PlayCount">How many times the animation plays; 0 means for ever. 1 for a still image.</param>
/// <param name="Frames">The composited frames in play order; for a still PNG, one frame holding the image.</param>
public sealed record PngFrameSequence(int CanvasWidth, int CanvasHeight, uint PlayCount, IReadOnlyList<PngFrame> Frames);

/// <summary>
/// Draws an animated PNG's frames onto one working canvas, following the APNG specification's output-buffer rules.
/// </summary>
/// <remarks>
/// <para>The canvas starts as fully transparent black. Before frame <c>i</c> is drawn, frame <c>i-1</c>'s
/// <c>dispose_op</c> is applied to its rectangle: NONE leaves it, BACKGROUND clears it to transparent black, PREVIOUS
/// puts back what it held before frame <c>i-1</c> was drawn. A first frame whose <c>dispose_op</c> is PREVIOUS is
/// treated as BACKGROUND (there is nothing before it to go back to). The last frame's disposal is never applied,
/// since nothing is drawn after it.</para>
/// <para>SOURCE copies the frame's pixels over the rectangle, alpha included. OVER composites them with
/// <see cref="Over"/>.</para>
/// </remarks>
internal sealed class PngCompositor
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly PngImage _image;
    private readonly PngAnimation _animation;
    private readonly int _canvasStride;

    // What the previous frame's disposal will do to its rectangle, applied at the start of the next frame.
    private PngDisposeOp _pendingDispose;
    private PngFrameControl? _pendingFrame;
    private byte[] _saved = [];     // the rectangle as it was before a PREVIOUS-disposing frame was drawn

    public PngCompositor(ReadOnlyMemory<byte> data, PngImage image)
    {
        _data = data;
        _image = image;
        _animation = image.Animation ?? throw new InvalidOperationException("The image has no animation");
        _canvasStride = checked(image.Width * 4);
        Canvas = new byte[checked(_canvasStride * image.Height)];
    }

    /// <summary>The working canvas, RGBA8; it holds the state after the last <see cref="Draw"/>.</summary>
    public byte[] Canvas { get; }

    public int FrameCount => _animation.Frames.Count;

    /// <summary>Applies the previous frame's disposal and then draws frame <paramref name="index"/>; frames must be drawn in order from 0.</summary>
    public void Draw(int index)
    {
        var frame = _animation.Frames[index];
        if (_pendingFrame is { } previous)
            Dispose(previous, _pendingDispose);

        // The first frame cannot go back to anything: PREVIOUS reads as BACKGROUND there.
        var dispose = index == 0 && frame.DisposeOp == PngDisposeOp.Previous ? PngDisposeOp.Background : frame.DisposeOp;
        if (dispose == PngDisposeOp.Previous)
            SaveRectangle(frame);
        _pendingFrame = frame;
        _pendingDispose = dispose;

        // Sub-image samples, then the existing sample to RGBA8 mapping on a view of the image with the frame's size.
        // Frame 0 of a file whose IDAT is that frame was inflated by Decode already.
        var samples = index == 0 && _animation.DefaultImageIsFirstFrame
            ? _image.Pixels
            : PngReader.DecodeFrameSamples(_data.Span, _image, index);
        var rgba = (_image with { Width = frame.Width, Height = frame.Height, Pixels = samples }).ToRgba8();

        var rowBytes = frame.Width * 4;
        for (var y = 0; y < frame.Height; y++)
        {
            var source = rgba.AsSpan(y * rowBytes, rowBytes);
            var target = Canvas.AsSpan((frame.YOffset + y) * _canvasStride + frame.XOffset * 4, rowBytes);
            if (frame.BlendOp == PngBlendOp.Source)
                source.CopyTo(target);
            else
                for (var x = 0; x < rowBytes; x += 4)
                    Over(source.Slice(x, 4), target.Slice(x, 4));
        }
    }

    private void Dispose(PngFrameControl frame, PngDisposeOp op)
    {
        if (op == PngDisposeOp.None)
            return;
        var rowBytes = frame.Width * 4;
        for (var y = 0; y < frame.Height; y++)
        {
            var target = Canvas.AsSpan((frame.YOffset + y) * _canvasStride + frame.XOffset * 4, rowBytes);
            if (op == PngDisposeOp.Background)
                target.Clear();
            else
                _saved.AsSpan(y * rowBytes, rowBytes).CopyTo(target);
        }
    }

    private void SaveRectangle(PngFrameControl frame)
    {
        var rowBytes = frame.Width * 4;
        var size = rowBytes * frame.Height;
        if (_saved.Length < size)
            _saved = new byte[size];
        for (var y = 0; y < frame.Height; y++)
            Canvas.AsSpan((frame.YOffset + y) * _canvasStride + frame.XOffset * 4, rowBytes)
                .CopyTo(_saved.AsSpan(y * rowBytes, rowBytes));
    }

    /// <summary>
    /// <c>source</c> composited over <c>target</c>, written into <c>target</c>: the OVER operation of the PNG
    /// specification's "Alpha channel processing", on straight RGBA8 pixels. With foreground alpha <c>af</c> and
    /// background alpha <c>ab</c> as fractions, <c>a = af + ab·(1 − af)</c>, and a colour channel is
    /// <c>(af·cf + (1 − af)·ab·cb) / a</c>, or 0 when <c>a</c> is 0.
    /// </summary>
    /// <remarks>
    /// Done exactly in integers rather than in floating point: with <c>af</c> and <c>ab</c> in 0..255 the numerator of
    /// <c>a</c> is <c>af·255 + ab·(255 − af)</c> (in units of 1/255²), and each channel is the rational
    /// <c>(af·255·cf + (255 − af)·ab·cb) / (af·255 + ab·(255 − af))</c>. Both results are rounded to nearest, halves up.
    /// The shortcuts (opaque or fully transparent foreground, fully transparent background) are what that arithmetic
    /// gives anyway, only without the divisions.
    /// </remarks>
    internal static void Over(ReadOnlySpan<byte> source, Span<byte> target)
    {
        int af = source[3];
        if (af == 255)
        {
            source.CopyTo(target);
            return;
        }
        int ab = target[3];
        if (af == 0)
        {
            // Nothing is added; the canvas stays as it was, except that a pixel with no alpha at all has no colour
            // either (the formula's "if alpha is 0, the colour is 0"), whatever its stored channels hold.
            if (ab == 0)
                target.Clear();
            return;
        }

        if (ab == 0)
        {
            source.CopyTo(target);
            return;
        }

        var alphaNumerator = af * 255 + ab * (255 - af);        // alpha out, in units of 1/255 of a 1/255 step
        var wf = af * 255;
        var wb = (255 - af) * ab;
        for (var c = 0; c < 3; c++)
            target[c] = (byte)((wf * source[c] + wb * target[c] + alphaNumerator / 2) / alphaNumerator);
        target[3] = (byte)((alphaNumerator + 127) / 255);
    }
}
