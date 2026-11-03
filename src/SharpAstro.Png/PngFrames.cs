using System.Runtime.Intrinsics;

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
    private readonly PngImage _image;
    private readonly PngAnimation _animation;
    private readonly int _canvasStride;

    // What the previous frame's disposal will do to its rectangle, applied at the start of the next frame.
    private PngDisposeOp _pendingDispose;
    private PngFrameControl? _pendingFrame;

    // Buffers kept from frame to frame and grown, never shrunk or reallocated per frame: the rectangle as it was before a
    // PREVIOUS-disposing frame was drawn, the frame's unfiltered samples, and one RGBA row for a colour type that is not
    // RGBA8 already. A compositor's whole footprint is the canvas, these three and the IDAT image Decode produced.
    private byte[] _saved = [];
    private byte[] _samples = [];
    private byte[] _row = [];

    public PngCompositor(PngImage image)
    {
        _image = image;
        _animation = image.Animation ?? throw new InvalidOperationException("The image has no animation");
        _canvasStride = checked(image.Width * 4);
        Canvas = new byte[checked(_canvasStride * image.Height)];
    }

    /// <summary>The working canvas, RGBA8; it holds the state after the last <see cref="Draw"/>.</summary>
    public byte[] Canvas { get; }

    public int FrameCount => _animation.Frames.Count;

    /// <summary>
    /// Test hook: take the scalar blend even where the vector one is available, so a test can run the same frames through
    /// both and require the same canvases. Not thread-safe to flip while another thread composites, which does not matter
    /// as the two paths give the same bytes.
    /// </summary>
    internal static bool ForceScalar { get; set; }

    /// <summary>Applies the previous frame's disposal and then draws frame <paramref name="index"/>; frames must be drawn in order from 0.</summary>
    /// <param name="data">The file the image was decoded from; the frames' compressed data is read from it.</param>
    /// <param name="index">The frame to draw.</param>
    public void Draw(ReadOnlySpan<byte> data, int index)
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

        // Sub-image samples: frame 0 of a file whose IDAT is that frame was inflated by Decode already; the others are
        // inflated into the one buffer kept between frames.
        ReadOnlySpan<byte> samples;
        if (index == 0 && _animation.DefaultImageIsFirstFrame)
            samples = _image.Pixels;
        else
        {
            var size = PngReader.SampleBytes(frame.Width, frame.Height, _image.SourceBitDepth, _image.ColorType);
            if (_samples.Length < size)
                _samples = GC.AllocateUninitializedArray<byte>(size);
            PngReader.DecodeFrameSamplesInto(data, _image, index, _samples);
            samples = _samples.AsSpan(0, size);
        }

        var step = _image.BitDepth == 16 ? 2 : 1;       // the same mapping PngImage.ToRgba8 applies
        var sourceStride = frame.Width * _image.SamplesPerPixel * step;
        var rowBytes = frame.Width * 4;
        // RGBA8 samples are already the pixels: they are blended from where they lie, with no expanded copy of the frame.
        var direct = _image.ColorType == 6 && step == 1;
        if (!direct && _row.Length < rowBytes)
            _row = new byte[rowBytes];
        var over = frame.BlendOp == PngBlendOp.Over;
        for (var y = 0; y < frame.Height; y++)
        {
            ReadOnlySpan<byte> source;
            if (direct)
                source = samples.Slice(y * sourceStride, rowBytes);
            else
            {
                var expanded = _row.AsSpan(0, rowBytes);
                PngImage.ExpandRowToRgba8(_image.ColorType, step, _image.Palette, _image.PaletteAlpha,
                    samples.Slice(y * sourceStride, sourceStride), expanded);
                source = expanded;
            }
            var target = Canvas.AsSpan((frame.YOffset + y) * _canvasStride + frame.XOffset * 4, rowBytes);
            if (over)
                OverRow(source, target);
            else
                source.CopyTo(target);
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
            _saved = GC.AllocateUninitializedArray<byte>(size);
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
    /// gives anyway, only without the divisions. So is the one for an opaque background (<c>ab = 255</c>, the common case
    /// of a frame drawn over a canvas that is already filled): the numerator of <c>a</c> is then 255², the alpha stays
    /// 255, and a channel is <c>(af·cf + (255 − af)·cb + 127) / 255</c>, which the tests check against the general form
    /// for every <c>af</c>, <c>cf</c> and <c>cb</c>. That is the form <see cref="OverRowVector"/> evaluates, four pixels at a time.
    /// </remarks>
    internal static void Over(ReadOnlySpan<byte> source, Span<byte> target)
    {
        int af = source[3];
        if (af == 255)
        {
            source[..4].CopyTo(target);
            return;
        }
        int ab = target[3];
        if (af == 0)
        {
            // Nothing is added; the canvas stays as it was, except that a pixel with no alpha at all has no colour
            // either (the formula's "if alpha is 0, the colour is 0"), whatever its stored channels hold.
            if (ab == 0)
                target[..4].Clear();
            return;
        }

        if (ab == 0)
        {
            source[..4].CopyTo(target);
            return;
        }

        if (ab == 255)
        {
            var nf = 255 - af;
            target[0] = (byte)((af * source[0] + nf * target[0] + 127) / 255);
            target[1] = (byte)((af * source[1] + nf * target[1] + 127) / 255);
            target[2] = (byte)((af * source[2] + nf * target[2] + 127) / 255);
            return;
        }

        var alphaNumerator = af * 255 + ab * (255 - af);        // alpha out, in units of 1/255 of a 1/255 step
        var wf = af * 255;
        var wb = (255 - af) * ab;
        for (var c = 0; c < 3; c++)
            target[c] = (byte)((wf * source[c] + wb * target[c] + alphaNumerator / 2) / alphaNumerator);
        target[3] = (byte)((alphaNumerator + 127) / 255);
    }

    /// <summary>A row of pixels (<paramref name="source"/> and <paramref name="target"/> the same length, a multiple of 4) through <see cref="Over"/>; vectorised where the hardware allows it.</summary>
    internal static void OverRow(ReadOnlySpan<byte> source, Span<byte> target)
    {
        if (Vector128.IsHardwareAccelerated && !ForceScalar)
            OverRowVector(source, target);
        else
            OverRowScalar(source, target);
    }

    /// <summary>The reference: <see cref="Over"/> pixel by pixel.</summary>
    internal static void OverRowScalar(ReadOnlySpan<byte> source, Span<byte> target)
    {
        for (var x = 0; x + 4 <= source.Length; x += 4)
            Over(source.Slice(x, 4), target.Slice(x, 4));
    }

    private static readonly Vector128<uint> AlphaLane = Vector128.Create(0xFF000000u);

    /// <summary>
    /// <see cref="OverRowScalar"/> four pixels at a time, byte for byte the same result. A group is looked at as a whole
    /// first: all four source pixels opaque is a copy; all four transparent over a canvas with no transparent pixel in
    /// the group changes nothing; all four canvas pixels opaque, whatever the source, is the <c>ab = 255</c> form of
    /// <see cref="Over"/> evaluated for every channel in 16-bit lanes (an exact division by 255 without dividing). Any other
    /// group, which is a partly transparent canvas under a partly transparent frame, goes through <see cref="Over"/> pixel
    /// by pixel, as does the tail of a row. Plain <c>Vector128</c> operations, no architecture-specific intrinsics.
    /// </summary>
    internal static void OverRowVector(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var x = 0;
        for (; x + 16 <= source.Length; x += 16)
        {
            var s = Vector128.Create(source.Slice(x, 16));
            var sa = s.AsUInt32();
            var dst = target.Slice(x, 16);
            if (Vector128.EqualsAll(sa | ~AlphaLane, Vector128<uint>.AllBitsSet))
            {
                s.CopyTo(dst);
                continue;
            }

            var t = Vector128.Create<byte>(dst);
            var ta = t.AsUInt32();
            if (Vector128.EqualsAll(sa & AlphaLane, Vector128<uint>.Zero))
            {
                if (!Vector128.EqualsAny(ta & AlphaLane, Vector128<uint>.Zero))
                    continue;
            }
            else if (Vector128.EqualsAll(ta | ~AlphaLane, Vector128<uint>.AllBitsSet))
            {
                // One 16-bit lane per channel: af·cf + (255 − af)·cb is at most 255·255, so with the 127 it still fits, and so does
                // t + 1 + (t >> 8), whose >> 8 is t / 255 exactly for every t that can occur here.
                var af = Vector128.Shuffle(s, Vector128.Create((byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15));
                var nf = Vector128.Create((byte)255) - af;
                var low = Blend(Vector128.WidenLower(af), Vector128.WidenLower(nf), Vector128.WidenLower(s), Vector128.WidenLower(t));
                var high = Blend(Vector128.WidenUpper(af), Vector128.WidenUpper(nf), Vector128.WidenUpper(s), Vector128.WidenUpper(t));
                // The alpha lanes were blended like colours (the frame's alpha as the foreground, 255 under it), which is
                // not what the canvas's alpha does here: it stays 255.
                (Vector128.Narrow(low, high).AsUInt32() | AlphaLane).AsByte().CopyTo(dst);
                continue;
            }

            for (var i = 0; i < 16; i += 4)
                Over(source.Slice(x + i, 4), target.Slice(x + i, 4));
        }

        for (; x + 4 <= source.Length; x += 4)
            Over(source.Slice(x, 4), target.Slice(x, 4));
    }

    private static Vector128<ushort> Blend(Vector128<ushort> af, Vector128<ushort> nf, Vector128<ushort> cf, Vector128<ushort> cb)
    {
        var t = af * cf + nf * cb + Vector128.Create((ushort)127);
        return (t + Vector128<ushort>.One + Vector128.ShiftRightLogical(t, 8)) >> 8;
    }
}
