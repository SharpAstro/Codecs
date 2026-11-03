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
/// This decodes a still, the first frame, composited onto the logical screen. Output is
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

    // Rows of an interlaced image arrive in four passes (GIF89a Appendix E): every 8th row from 0,
    // every 8th from 4, every 4th from 2, every 2nd from 1.
    private static readonly int[] PassStart = [0, 4, 2, 1];
    private static readonly int[] PassStep = [8, 8, 4, 2];

    private static bool DecodeFrame0(ReadOnlySpan<byte> data, GifContainer container, Span<byte> rgba)
    {
        var frame = container.Frames[0];
        var f = frame.Info;
        int screenWidth = container.Info.Width;

        // Palette as packed RGBA; entries past the table's end (an index the table is too small for)
        // and the transparent index are resolved here so the pixel loop is one lookup.
        Span<uint> palette = stackalloc uint[256];
        BuildPalette(data, frame, palette);

        // The canvas starts as ImageMagick's coalesced frame 0 holds it (observed, Magick.NET Q16-HDRI,
        // with hand-built files and the logical screen's background index varied): every pixel the frame
        // does not cover is the global table's background-index colour, opaque, or with alpha 0 when the
        // frame has a transparent index (the RGB stays the background's). Without a global table it is
        // white, ImageMagick's default background. Pixels the LZW data never reached stay as this too.
        // Browsers ignore the background colour; that policy is the caller's, which is why the
        // background index is reported on GifInfo.
        uint canvas = 0xFFFFFFFFu;
        if (container.Info.HasGlobalColorTable)
        {
            // An index past the table is clamped to its last entry (Magick's answer for index 7 of 4).
            int o = GifContainer.GlobalTableOffset + Math.Min(container.Info.BackgroundColorIndex, container.Info.GlobalColorTableSize - 1) * 3;
            canvas = data[o] | ((uint)data[o + 1] << 8) | ((uint)data[o + 2] << 16) | 0xFF000000u;
        }
        if (f.TransparentIndex >= 0)
        {
            canvas &= 0x00FFFFFFu;
        }
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)canvas;
            rgba[i + 1] = (byte)(canvas >> 8);
            rgba[i + 2] = (byte)(canvas >> 16);
            rgba[i + 3] = (byte)(canvas >> 24);
        }

        var indices = new byte[checked(f.Width * f.Height)];
        var lzwInput = GifContainer.GatherSubBlocks(data, frame.DataOffset, frame.DataLength);
        int decoded = Lzw.Decode(LzwFormat.Gif(f.MinCodeSize), lzwInput, indices);

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
            var dst = rgba.Slice(((f.Y + targetRow) * screenWidth + f.X) * 4, count * 4);
            for (int x = 0; x < count; x++)
            {
                uint c = palette[src[x]];
                dst[x * 4] = (byte)c;
                dst[x * 4 + 1] = (byte)(c >> 8);
                dst[x * 4 + 2] = (byte)(c >> 16);
                dst[x * 4 + 3] = (byte)(c >> 24);
            }
        }

        return decoded < indices.Length;
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
