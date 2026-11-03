using System.Buffers.Binary;
using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Png;

/// <summary>
/// The pixel loops behind <see cref="PngImageDecoder.TryDecodeRgbaFloat(ReadOnlySpan{byte}, out float[], out int, out int, out ColorEncoding)"/>:
/// a decoded <see cref="PngImage"/> to linear-light RGBA floats in a caller-sized array.
/// </summary>
internal static class PngFloatFill
{
    /// <summary>
    /// Fills <paramref name="dst"/> (<c>width * height * 4</c> floats) with the image's linear-light RGBA.
    /// </summary>
    /// <exception cref="InvalidDataException">An indexed pixel past the palette's end, or an indexed
    /// image with no palette.</exception>
    public static void Fill(PngImage png, ColorEncoding source, float[] dst) => FillReference(png, source, dst);

    // ---- the scalar reference ------------------------------------------------------------------

    // Fills dst (width * height * 4 floats) with the image's linear-light RGBA, one row at a time so the
    // working set beyond dst is a couple of rows. Per row: gather the colour codes (alpha goes straight to
    // dst, since it is not light and must not meet the range expansion or the curve), expand them to
    // signals, linearise them, scatter. Everything numeric is PngHdr's.
    internal static void FillReference(PngImage png, ColorEncoding source, float[] dst)
    {
        int w = png.Width, h = png.Height;
        var pixels = png.Pixels;
        bool fullRange = source.FullRange;

        if (png.ColorType == 3)
        {
            FillIndexedReference(png, source, dst);
            return;
        }

        bool sixteen = png.BitDepth == 16;
        int spp = png.SamplesPerPixel;
        int colorChannels = png.ColorType is 0 or 4 ? 1 : 3;
        bool hasAlpha = png.ColorType is 4 or 6;
        // Alpha is 0..1 over the sample's own full range: never range-expanded or curved, and divided (not
        // multiplied by a reciprocal) so it is the same correctly-rounded value as a colour code's signal.
        float alphaMax = sixteen ? 65535f : 255f;
        int rowBytes = w * spp * (sixteen ? 2 : 1);

        var codes = new ushort[w * colorChannels];
        var signal = new float[w * colorChannels];

        for (int y = 0; y < h; y++)
        {
            var row = pixels.AsSpan(y * rowBytes, rowBytes);
            int outBase = y * w * 4;

            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < colorChannels; c++)
                    codes[x * colorChannels + c] = ReadSample(row, x * spp + c, sixteen);

                dst[outBase + x * 4 + 3] = hasAlpha ? ReadSample(row, x * spp + colorChannels, sixteen) / alphaMax : 1f;
            }

            PngHdr.ExpandCodes(codes, sixteen ? 16 : 8, fullRange, signal);
            PngHdr.ToLinear(source.Transfer, source.Primaries, signal, colorChannels, colorChannels);

            for (int x = 0; x < w; x++)
            {
                int d = outBase + x * 4;
                if (colorChannels == 1)
                {
                    dst[d] = dst[d + 1] = dst[d + 2] = signal[x];
                }
                else
                {
                    dst[d] = signal[x * 3];
                    dst[d + 1] = signal[x * 3 + 1];
                    dst[d + 2] = signal[x * 3 + 2];
                }
            }
        }
    }

    // Indexed: the palette is at most 256 entries, so it is expanded and linearised once as a table and
    // the pixels just look their entry up (HLG couples a pixel's three channels, which is fine here: an
    // entry is one pixel). tRNS alpha is one byte per entry; entries past its end are opaque.
    private static void FillIndexedReference(PngImage png, ColorEncoding source, float[] dst)
    {
        if (png.Palette is not { } palette)
            throw new InvalidDataException("Indexed PNG has no palette to expand to RGBA");

        int entries = palette.Length / 3;
        var table = new float[entries * 3];
        PngHdr.ExpandCodes(palette.AsSpan(0, entries * 3), source.FullRange, table);
        PngHdr.ToLinear(source.Transfer, source.Primaries, table, 3, 3);

        var paletteAlpha = png.PaletteAlpha;
        var pixels = png.Pixels;
        int count = png.Width * png.Height;
        for (int i = 0; i < count; i++)
        {
            int index = pixels[i];
            if (index >= entries)
                throw new InvalidDataException($"Palette index {index} out of range (palette has {entries} entries)");

            int d = i * 4;
            dst[d] = table[index * 3];
            dst[d + 1] = table[index * 3 + 1];
            dst[d + 2] = table[index * 3 + 2];
            dst[d + 3] = paletteAlpha is not null && index < paletteAlpha.Length ? paletteAlpha[index] / 255f : 1f;
        }
    }

    // Sample i of a row: one byte, or two big-endian ones (PNG's order) for 16-bit.
    private static ushort ReadSample(ReadOnlySpan<byte> row, int i, bool sixteen) =>
        sixteen ? BinaryPrimitives.ReadUInt16BigEndian(row.Slice(i * 2, 2)) : row[i];
}
