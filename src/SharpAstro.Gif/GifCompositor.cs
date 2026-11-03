using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Gif;

/// <summary>
/// Draws GIF frames onto a logical-screen canvas of tightly packed non-premultiplied RGBA8. The still decoder
/// draws frame 0 through <see cref="FillInitialCanvas"/> + <see cref="DrawFrame"/>; <see cref="GifCompositor"/> runs the
/// whole animation over one canvas on top of the same two functions, so a still and the first frame of an
/// animation cannot differ.
/// <para>
/// What GIF89a leaves open was settled by observing Magick.NET's <c>Coalesce()</c> (Q16-HDRI), with hand-built
/// files, and is what these functions reproduce exactly:
/// </para>
/// <list type="bullet">
/// <item>The canvas before frame 0 is the global table's background-index colour, opaque, or alpha 0 (same RGB) when
/// frame 0 has a transparent index; white without a global table. See <see cref="FillInitialCanvas"/>.</item>
/// <item>Frame 0 is copied: a pixel of its transparent index lands as the palette colour with alpha 0.</item>
/// <item>A later frame is drawn "over": an opaque pixel replaces the canvas pixel; a transparent one leaves an opaque
/// canvas pixel alone and, where the canvas pixel is already alpha 0, makes it 0,0,0,0 (Magick's Over of two
/// transparent pixels, whatever RGB they held).</item>
/// </list>
/// </summary>
internal static class GifPixels
{
    // Rows of an interlaced image arrive in four passes (GIF89a Appendix E): every 8th row from 0,
    // every 8th from 4, every 4th from 2, every 2nd from 1.
    private static readonly int[] PassStart = [0, 4, 2, 1];
    private static readonly int[] PassStep = [8, 8, 4, 2];

    /// <summary>
    /// The canvas as ImageMagick's coalesced frame 0 holds it (observed, with the logical screen's background index
    /// varied): the global table's colour at the background index, opaque, or with alpha 0 when frame 0 has a
    /// transparent index (the RGB stays the background's). Without a global table it is white, ImageMagick's
    /// default background. An index past the table is clamped to its last entry (Magick's answer for index 7 of 4).
    /// Browsers ignore the background colour; that policy is the caller's, which is why the background index is
    /// reported on <see cref="GifInfo"/>.
    /// </summary>
    internal static void FillInitialCanvas(ReadOnlySpan<byte> data, GifContainer container, Span<byte> canvas)
    {
        uint colour = 0xFFFFFFFFu;
        if (container.Info.HasGlobalColorTable)
        {
            int o = GifContainer.GlobalTableOffset + Math.Min(container.Info.BackgroundColorIndex, container.Info.GlobalColorTableSize - 1) * 3;
            colour = data[o] | ((uint)data[o + 1] << 8) | ((uint)data[o + 2] << 16) | 0xFF000000u;
        }
        if (container.Frames[0].Info.TransparentIndex >= 0)
        {
            colour &= 0x00FFFFFFu;
        }
        for (int i = 0; i < canvas.Length; i += 4)
        {
            canvas[i] = (byte)colour;
            canvas[i + 1] = (byte)(colour >> 8);
            canvas[i + 2] = (byte)(colour >> 16);
            canvas[i + 3] = (byte)(colour >> 24);
        }
    }

    /// <summary>
    /// Decodes frame <paramref name="index"/>'s pixels onto <paramref name="canvas"/>. Pixels the LZW data never
    /// reached are left as they were. <paramref name="indices"/> is a grow-only scratch buffer for the frame's palette indices.
    /// </summary>
    /// <returns>True when the frame's pixels came up short.</returns>
    internal static bool DrawFrame(ReadOnlySpan<byte> data, GifContainer container, int index, Span<byte> canvas, ref byte[] indices)
    {
        var frame = container.Frames[index];
        var f = frame.Info;
        int screenWidth = container.Info.Width;

        // Palette as packed RGBA; entries past the table's end (an index the table is too small for)
        // and the transparent index are resolved here so the pixel loop is one lookup.
        Span<uint> palette = stackalloc uint[256];
        BuildPalette(data, frame, palette);

        int pixels = checked(f.Width * f.Height);
        if (indices.Length < pixels)
        {
            indices = new byte[pixels];
        }
        var lzwInput = GifContainer.GatherSubBlocks(data, frame.DataOffset, frame.DataLength);
        int decoded = Lzw.Decode(LzwFormat.Gif(f.MinCodeSize), lzwInput, indices.AsSpan(0, pixels));

        bool over = index > 0;

        // Row of the screen that file row r lands on; pass state advances monotonically.
        int pass = 0;
        int row = 0;
        int rowsToPaint = (decoded + f.Width - 1) / f.Width;
        for (int fileRow = 0; fileRow < rowsToPaint; fileRow++)
        {
            int targetRow;
            if (f.Interlaced)
            {
                // A pass with no row (start >= height) is skipped; pass reaches 3 at most, whose start
                // is 1 and which has a row whenever height > 1, and fileRow < height bounds the loop.
                while (row >= f.Height && pass < 3)
                {
                    pass++;
                    row = PassStart[pass];
                }
                targetRow = row;
                row += PassStep[pass];
            }
            else
            {
                targetRow = fileRow;
            }

            int count = Math.Min(f.Width, decoded - fileRow * f.Width);
            var src = indices.AsSpan(fileRow * f.Width, count);
            var dst = canvas.Slice(((f.Y + targetRow) * screenWidth + f.X) * 4, count * 4);
            for (int x = 0; x < count; x++)
            {
                if (over && src[x] == f.TransparentIndex)
                {
                    if (dst[x * 4 + 3] == 0)
                    {
                        dst.Slice(x * 4, 4).Clear();
                    }
                    continue;
                }

                uint c = palette[src[x]];
                dst[x * 4] = (byte)c;
                dst[x * 4 + 1] = (byte)(c >> 8);
                dst[x * 4 + 2] = (byte)(c >> 16);
                dst[x * 4 + 3] = (byte)(c >> 24);
            }
        }

        return decoded < pixels;
    }

    private static void BuildPalette(ReadOnlySpan<byte> data, GifFrameLayout frame, Span<uint> palette)
    {
        var f = frame.Info;
        for (int i = 0; i < 256; i++)
        {
            if (i < f.ColorTableSize)
            {
                int o = frame.PaletteOffset + i * 3;
                palette[i] = data[o] | ((uint)data[o + 1] << 8) | ((uint)data[o + 2] << 16) | 0xFF000000u;
            }
            else
            {
                // An index past the table, or no table at all (Magick reads that as an all-black
                // palette; for an index past the end it refuses the file, so this one is ours): opaque black.
                palette[i] = 0xFF000000u;
            }
        }

        // The transparent index keeps its colour and loses its alpha, as Magick's coalesced pixels do
        // (a palette entry of 0,255,0 reads 0,255,0,0): no information is thrown away, and a caller
        // that wants premultiplied pixels multiplies.
        if (f.TransparentIndex >= 0)
        {
            palette[f.TransparentIndex] &= 0x00FFFFFFu;
        }
    }
}

/// <summary>
/// Composites a GIF's frames, in order, onto one working canvas. Before frame <c>i</c> is drawn, frame <c>i-1</c>'s
/// disposal is applied to its rectangle; the last frame's disposal is never applied, since nothing is drawn after it.
/// <list type="bullet">
/// <item>0, 1 and 4-7: the canvas is left as the frame drew it. Magick's Coalesce treats the reserved values 4-7 as
/// "none" (observed on every one of them), as does this.</item>
/// <item>2 (restore to background): the rectangle becomes alpha 0 and <b>keeps its RGB</b>. That is what Coalesce
/// leaves (observed: a cleared red pixel reads 255,0,0,0, not 0,0,0,0 and not the background colour), so it is
/// reproduced rather than "fixed" to transparent black.</item>
/// <item>3 (restore to previous): the rectangle goes back to what the canvas held before the frame was drawn. Only that
/// rectangle is saved, and only for a frame that asks for it. On frame 0 "previous" is the initial canvas
/// (<see cref="GifPixels.FillInitialCanvas"/>), as in Coalesce, not "background".</item>
/// </list>
/// </summary>
internal sealed class GifCompositor
{
    private readonly GifContainer _container;
    private byte[] _indices = [];
    private byte[] _snapshot = [];
    private int _next;

    public GifCompositor(GifContainer container)
    {
        _container = container;
        Canvas = new byte[checked(container.Info.Width * container.Info.Height * 4)];
    }

    /// <summary>The working canvas, RGBA8; it holds the state after the last <see cref="Draw"/>.</summary>
    public byte[] Canvas { get; }

    public int FrameCount => _container.Frames.Length;

    /// <summary>Index <see cref="Draw"/> will draw next.</summary>
    public int NextIndex => _next;

    /// <summary>Disposes the frame before, then draws frame <see cref="NextIndex"/>. Returns true when its pixels came up short.</summary>
    public bool DrawNext(ReadOnlySpan<byte> data)
    {
        int index = _next;
        if (index >= FrameCount)
        {
            throw new InvalidOperationException("Every frame has been drawn.");
        }

        var frames = _container.Frames;
        int screenStride = _container.Info.Width * 4;
        if (index == 0)
        {
            GifPixels.FillInitialCanvas(data, _container, Canvas);
        }
        else
        {
            var previous = frames[index - 1].Info;
            if (previous.DisposalMethod == 2)
            {
                for (int y = 0; y < previous.Height; y++)
                {
                    int rowStart = ((previous.Y + y) * _container.Info.Width + previous.X) * 4;
                    for (int x = 0; x < previous.Width; x++)
                    {
                        Canvas[rowStart + x * 4 + 3] = 0;
                    }
                }
            }
            else if (previous.DisposalMethod == 3)
            {
                RestoreRectangle(previous, screenStride);
            }
        }

        var current = frames[index].Info;
        if (current.DisposalMethod == 3)
        {
            SaveRectangle(current, screenStride);
        }

        bool truncated = GifPixels.DrawFrame(data, _container, index, Canvas, ref _indices);
        _next++;
        return truncated;
    }

    private void SaveRectangle(GifFrameInfo f, int screenStride)
    {
        int rowBytes = f.Width * 4;
        int size = checked(rowBytes * f.Height);
        if (_snapshot.Length < size)
        {
            _snapshot = new byte[size];
        }
        for (int y = 0; y < f.Height; y++)
        {
            Canvas.AsSpan((f.Y + y) * screenStride + f.X * 4, rowBytes).CopyTo(_snapshot.AsSpan(y * rowBytes));
        }
    }

    private void RestoreRectangle(GifFrameInfo f, int screenStride)
    {
        int rowBytes = f.Width * 4;
        for (int y = 0; y < f.Height; y++)
        {
            _snapshot.AsSpan(y * rowBytes, rowBytes).CopyTo(Canvas.AsSpan((f.Y + y) * screenStride + f.X * 4));
        }
    }
}
