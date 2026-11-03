using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Gif;

/// <summary>A decoded still: frame 0 composited onto the logical screen, in tightly packed non-premultiplied RGBA8.</summary>
public sealed class GifImage
{
    internal GifImage(int width, int height, byte[] rgba, bool isTruncated, GifInfo info)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        IsTruncated = isTruncated;
        Info = info;
    }

    /// <summary>Logical screen width in pixels.</summary>
    public int Width { get; }

    /// <summary>Logical screen height in pixels.</summary>
    public int Height { get; }

    /// <summary>Tightly packed, non-premultiplied RGBA8 pixels; alpha is 0 or 255.</summary>
    public byte[] Rgba { get; }

    /// <summary>
    /// True when the first frame's pixels came up short: the LZW stream or the file ended before the
    /// frame was full. The pixels that were there are decoded; the rest keep the canvas colour (the background, see <see cref="GifDecoder"/>).
    /// </summary>
    public bool IsTruncated { get; }

    /// <summary>The file's structure, as <see cref="GifDecoder.ReadInfo"/> reports it.</summary>
    public GifInfo Info { get; }
}

/// <summary>What <see cref="GifDecoder.DecodeTo"/> learned while decoding into the caller's buffer.</summary>
/// <param name="IsTruncated">As <see cref="GifImage.IsTruncated"/>.</param>
/// <param name="Info">The file's structure.</param>
public readonly record struct GifDecodeResult(bool IsTruncated, GifInfo Info);

/// <summary>
/// Decodes GIF87a / GIF89a. Written from the GIF89a specification; what it leaves open (the background around a small first frame,
/// a missing colour table) was settled by observing Magick.NET. Reading only: there is no encoder.
/// <para>
/// <see cref="Decode"/> / <see cref="DecodeTo"/> decode a still, the first frame, composited onto the logical screen;
/// <see cref="DecodeAnimation(ReadOnlySpan{byte})"/> and <see cref="GifAnimationDecoder"/> composite every frame. Output is
/// non-premultiplied RGBA8 where a pixel is either opaque or has alpha 0 (a transparent palette index keeps its RGB).
/// Malformed input throws <see cref="InvalidDataException"/>; input that is merely incomplete (a
/// truncated LZW stream, a missing trailer, bytes after the trailer) decodes what is there and says so
/// through <see cref="GifImage.IsTruncated"/> / <see cref="GifInfo.HasTrailer"/> /
/// <see cref="GifInfo.UnparsedByteCount"/>. A logical screen above 2^26 pixels or a frame outside
/// it is refused before any raster is allocated.
/// </para>
/// </summary>
public static class GifDecoder
{
    /// <summary>
    /// Reads the logical screen and every frame's headers without decoding any pixels. Returns false
    /// instead of throwing for malformed data.
    /// </summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out GifInfo info)
    {
        try
        {
            info = GifContainer.Read(data).Info;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            info = null!;
            return false;
        }
    }

    /// <summary>Reads the logical screen and every frame's headers without decoding any pixels.</summary>
    /// <exception cref="InvalidDataException">The data is not a usable GIF, or declares a screen or frame the limits refuse.</exception>
    public static GifInfo ReadInfo(ReadOnlySpan<byte> data) => GifContainer.Read(data).Info;

    /// <summary>Decodes the first frame onto the logical screen into a new RGBA8 array.</summary>
    /// <exception cref="InvalidDataException">The data is malformed (see the class remarks).</exception>
    public static GifImage Decode(ReadOnlySpan<byte> data)
    {
        var container = GifContainer.Read(data);
        var info = container.Info;
        var rgba = new byte[checked(info.Width * info.Height * 4)];
        bool truncated = DecodeFrame0(data, container, rgba);
        return new GifImage(info.Width, info.Height, rgba, truncated, info);
    }

    /// <summary>
    /// Decodes the first frame onto the logical screen into a caller-owned, tightly packed RGBA8 buffer.
    /// </summary>
    /// <param name="data">The complete GIF file.</param>
    /// <param name="destination">At least width × height × 4 bytes (size it from <see cref="ReadInfo"/>); only that prefix is written.</param>
    /// <exception cref="InvalidDataException">The data is malformed (see the class remarks).</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static GifDecodeResult DecodeTo(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var container = GifContainer.Read(data);
        var info = container.Info;
        int required = checked(info.Width * info.Height * 4);
        if (destination.Length < required)
        {
            throw new ArgumentException("Destination is smaller than width × height × 4 bytes.", nameof(destination));
        }

        bool truncated = DecodeFrame0(data, container, destination[..required]);
        return new GifDecodeResult(truncated, info);
    }


    private static bool DecodeFrame0(ReadOnlySpan<byte> data, GifContainer container, Span<byte> rgba)
    {
        // Pixels the LZW data never reached stay as the initial canvas.
        GifPixels.FillInitialCanvas(data, container, rgba);
        byte[] indices = [];
        return GifPixels.DrawFrame(data, container, 0, rgba, ref indices);
    }

    /// <summary>
    /// The default ceiling on the total pixels <see cref="DecodeAnimation(ReadOnlySpan{byte})"/> may materialise:
    /// logical screen width × height × frame count, 2^28 (the figure SharpAstro.Webp and SharpAstro.Png use). A caller with
    /// a tighter budget passes a smaller limit to <see cref="DecodeAnimation(ReadOnlySpan{byte}, long)"/>, or streams with
    /// <see cref="GifAnimationDecoder"/>, which never holds more than one canvas.
    /// </summary>
    public const long DefaultMaximumAnimationPixels = 1L << 28;

    /// <summary>
    /// Decodes every frame to its own full composited canvas, RGBA8, with <see cref="DefaultMaximumAnimationPixels"/> as the limit.
    /// </summary>
    /// <param name="data">A still or animated GIF file.</param>
    /// <exception cref="InvalidDataException">The data is malformed, or canvas × frame count exceeds <see cref="DefaultMaximumAnimationPixels"/>.</exception>
    public static GifAnimation DecodeAnimation(ReadOnlySpan<byte> data) =>
        DecodeAnimation(data, DefaultMaximumAnimationPixels);

    /// <summary>
    /// Decodes every frame to its own full composited canvas, RGBA8 (disposal methods applied, see
    /// <see cref="GifAnimationDecoder"/> for the exact rules), refusing an animation whose logical screen × frame count is above
    /// <paramref name="maximumTotalPixels"/> before any canvas is allocated. A still GIF gives one frame.
    /// </summary>
    /// <remarks>
    /// The check runs on the frame headers the parse found, so it sees the frame count the file really has, which the input's
    /// length bounds: a frame costs at least about 15 bytes however large the screen, so a few hundred bytes can declare dozens
    /// of frames of an 8192 × 8192 screen. Frames come out in file order. A file that ends inside a frame's pixel data yields
    /// that frame partly drawn (the rest as the canvas had it) with <see cref="GifAnimationFrame.IsTruncated"/> set, and
    /// <see cref="GifAnimation.IsTruncated"/> says so for the animation; frames before it are exactly what a whole file gives.
    /// </remarks>
    /// <param name="data">A still or animated GIF file.</param>
    /// <param name="maximumTotalPixels">The most pixels all returned frames may hold together.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumTotalPixels"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">The data is malformed, or canvas × frame count exceeds <paramref name="maximumTotalPixels"/>.</exception>
    public static GifAnimation DecodeAnimation(ReadOnlySpan<byte> data, long maximumTotalPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTotalPixels);
        var container = GifContainer.Read(data);
        var info = container.Info;

        // Every returned frame is a full-canvas copy, so memory is canvas × frames. The screen is at most 2^26 pixels and the
        // frame count is bounded by the input's length, so the product fits a long.
        long totalPixels = (long)info.Width * info.Height * container.Frames.Length;
        if (totalPixels > maximumTotalPixels)
        {
            throw new InvalidDataException(
                $"GIF animation of {container.Frames.Length} frames of {info.Width}x{info.Height} needs {totalPixels} pixels, over the {maximumTotalPixels}-pixel limit.");
        }

        var compositor = new GifCompositor(container);
        var frames = new GifAnimationFrame[container.Frames.Length];
        bool anyTruncated = false;
        for (int i = 0; i < frames.Length; i++)
        {
            bool truncated = compositor.DrawNext(data);
            anyTruncated |= truncated;
            frames[i] = GifAnimationFrame.Create(i, (byte[])compositor.Canvas.Clone(), container.Frames[i].Info, truncated);
        }

        return new GifAnimation(info.Width, info.Height, info.LoopCount, frames, anyTruncated, info);
    }
}

/// <summary>One composited frame of a GIF: the whole logical screen as it looks once the frame is drawn, with the frame's own headers.</summary>
/// <remarks>
/// <see cref="Rgba"/> is tightly packed non-premultiplied RGBA8, <c>Width × Height × 4</c> bytes. A frame from
/// <see cref="GifDecoder.DecodeAnimation(ReadOnlySpan{byte})"/> owns it; a frame from <see cref="GifAnimationDecoder"/> shares the
/// decoder's one working canvas and its pixels are only valid until the decoder is advanced.
/// </remarks>
public sealed record GifAnimationFrame
{
    private GifAnimationFrame(int index, byte[] rgba, GifFrameInfo frame, bool isTruncated)
    {
        Index = index;
        Rgba = rgba;
        Frame = frame;
        IsTruncated = isTruncated;
    }

    internal static GifAnimationFrame Create(int index, byte[] rgba, GifFrameInfo frame, bool isTruncated) =>
        new(index, rgba, frame, isTruncated);

    /// <summary>The frame's zero-based index in play order.</summary>
    public int Index { get; }

    /// <summary>The canvas after this frame was drawn.</summary>
    public byte[] Rgba { get; }

    /// <summary>The frame's headers: rectangle, delay, disposal, transparency, palette.</summary>
    public GifFrameInfo Frame { get; }

    /// <summary>The Graphic Control Extension delay in 1/100 s, raw: 0 stays 0, no "0 means 100 ms" browser policy is applied.</summary>
    public int DelayCentiseconds => Frame.DelayCentiseconds;

    /// <summary>The frame's disposal method as written (0-7), applied to its rectangle before the next frame is drawn.</summary>
    public int DisposalMethod => Frame.DisposalMethod;

    /// <summary>True when the frame's pixels came up short: the file or its LZW stream ended before the frame was full.</summary>
    public bool IsTruncated { get; }
}

/// <summary>A GIF animation with every frame composited and materialised: see <see cref="GifDecoder.DecodeAnimation(ReadOnlySpan{byte})"/>.</summary>
/// <param name="Width">The logical screen width in pixels, which is every frame's width.</param>
/// <param name="Height">The logical screen height in pixels.</param>
/// <param name="LoopCount">The NETSCAPE2.0 / ANIMEXTS1.0 loop count (0 = for ever), or null when the file has none.</param>
/// <param name="Frames">The composited frames in play order; a still GIF has one.</param>
/// <param name="IsTruncated">True when any frame's pixels came up short.</param>
/// <param name="Info">The file's structure.</param>
public sealed record GifAnimation(
    int Width,
    int Height,
    int? LoopCount,
    IReadOnlyList<GifAnimationFrame> Frames,
    bool IsTruncated,
    GifInfo Info);

/// <summary>
/// Streams a GIF's frames through one working canvas: <see cref="MoveNext"/> disposes the frame before, draws the next and
/// exposes the canvas as <see cref="Current"/>. Memory is the canvas, one scratch of palette indices, and, only when a frame
/// asks for disposal method 3, a copy of that frame's rectangle; it does not grow with the frame count.
/// </summary>
/// <remarks>
/// <para><b>Frame reuse.</b> <see cref="GifAnimationFrame.Rgba"/> of <see cref="Current"/> is the decoder's own canvas, the same array
/// for every frame: it is only valid until the next <see cref="MoveNext"/>. Copy it to keep it, or use
/// <see cref="GifDecoder.DecodeAnimation(ReadOnlySpan{byte})"/>, which does.</para>
/// <para><b>Compositing</b> follows GIF89a, with what it leaves open settled by observing Magick.NET's <c>Coalesce()</c>. A frame
/// draws only its rectangle; a pixel of its transparent index leaves the canvas as it was. Before a frame is drawn the frame
/// before it is disposed: methods 0, 1 and the reserved 4-7 leave the canvas, 2 clears the rectangle to alpha 0 (the RGB it held
/// stays, as Coalesce leaves it), 3 puts back what the rectangle held before that frame was drawn. The last frame's disposal is never
/// applied. The canvas before frame 0 is the global table's background colour (alpha 0 when frame 0 has a transparent index,
/// white without a global table).</para>
/// <para>The input is held, not copied, and must stay unchanged while the decoder is in use.</para>
/// </remarks>
public sealed class GifAnimationDecoder
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly GifContainer _container;
    private readonly GifCompositor _compositor;
    private GifAnimationFrame? _current;

    /// <summary>Parses the file's structure and allocates the one canvas; no pixel is decoded yet.</summary>
    /// <param name="data">The complete GIF file.</param>
    /// <exception cref="InvalidDataException">The data is not a usable GIF, or its logical screen is over the limit.</exception>
    public GifAnimationDecoder(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _container = GifContainer.Read(data.Span);
        _compositor = new GifCompositor(_container);
    }

    /// <summary>The logical screen width in pixels.</summary>
    public int Width => _container.Info.Width;

    /// <summary>The logical screen height in pixels.</summary>
    public int Height => _container.Info.Height;

    /// <summary>The number of frames in the file.</summary>
    public int FrameCount => _container.Frames.Length;

    /// <summary>The loop count (0 = for ever), or null when the file has no NETSCAPE2.0 / ANIMEXTS1.0 extension.</summary>
    public int? LoopCount => _container.Info.LoopCount;

    /// <summary>The file's structure.</summary>
    public GifInfo Info => _container.Info;

    /// <summary>True when any frame drawn so far came up short.</summary>
    public bool IsTruncated { get; private set; }

    /// <summary>The frame <see cref="MoveNext"/> last drew; throws before the first call. Its pixels are valid until the next call.</summary>
    /// <exception cref="InvalidOperationException">No frame has been drawn yet.</exception>
    public GifAnimationFrame Current =>
        _current ?? throw new InvalidOperationException("Call MoveNext before reading Current.");

    /// <summary>Disposes the previous frame and draws the next one onto the canvas.</summary>
    /// <returns>True when a frame was drawn; false after the last.</returns>
    public bool MoveNext()
    {
        int index = _compositor.NextIndex;
        if (index >= _compositor.FrameCount)
        {
            return false;
        }

        bool truncated = _compositor.DrawNext(_data.Span);
        IsTruncated |= truncated;
        _current = GifAnimationFrame.Create(index, _compositor.Canvas, _container.Frames[index].Info, truncated);
        return true;
    }
}
