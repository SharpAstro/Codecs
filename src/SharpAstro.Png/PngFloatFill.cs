using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
    /// Bit-identical to <see cref="FillReference"/>: every colour sample is a lookup in a table that
    /// <see cref="PngHdr"/> built with the reference's own arithmetic (see <see cref="PngHdr.BuildLinearTable"/>),
    /// so there is no per-sample curve and no scratch row. HLG RGB keeps its per-pixel OOTF, which couples
    /// the channels, on top of a scene-light table.
    /// </summary>
    /// <exception cref="InvalidDataException">An indexed pixel past the palette's end, or an indexed
    /// image with no palette.</exception>
    public static void Fill(PngImage png, ColorEncoding source, float[] dst)
    {
        if (png.ColorType == 3)
        {
            FillIndexed(png, source, dst);
            return;
        }

        bool sixteen = png.BitDepth == 16;
        int tableLength = sixteen ? 65536 : 256;
        int colorChannels = png.ColorType is 0 or 4 ? 1 : 3;

        // A table costs one curve evaluation per entry, which is what the reference pays per sample, so it
        // only pays for itself once the image has as many colour samples as the table has entries. A tiny
        // image takes the reference path (it is the same arithmetic, just not amortised).
        if ((long)png.Width * png.Height * colorChannels < tableLength)
        {
            FillReference(png, source, dst);
            return;
        }

        bool hlgRgb = source.Transfer == TransferFunction.Hlg && colorChannels == 3;
        int bitDepth = sixteen ? 16 : 8;
        // The 8-bit tables live on the stack (2 KB, unused at 16 bits). The 16-bit one is 256 KB, which the
        // pool hands back for the next decode instead of the large-object heap seeing a new one per call.
        Span<float> stackTable = stackalloc float[256];
        Span<float> alpha = stackalloc float[256];
        float[]? rented = null;
        try
        {
            Span<float> table = stackTable;
            if (sixteen)
            {
                rented = ArrayPool<float>.Shared.Rent(tableLength);
                table = rented.AsSpan(0, tableLength);
            }

            if (hlgRgb)
                PngHdr.BuildHlgSceneLightTable(bitDepth, source.FullRange, table);
            else
                PngHdr.BuildLinearTable(source.Transfer, source.Primaries, bitDepth, source.FullRange, table);

            var weights = LumaWeights.For(source.Primaries);
            if (sixteen)
            {
                FillRows<Sample16>(png, table, default, hlgRgb, weights, dst);
            }
            else
            {
                // Alpha is a/255f, 256 values: a table is cheaper than 256-fold division, and the same value.
                for (int a = 0; a < 256; a++)
                    alpha[a] = a / 255f;
                FillRows<Sample8>(png, table, alpha, hlgRgb, weights, dst);
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<float>.Shared.Return(rented);
        }
    }

    // ---- the table fast path ----------------------------------------------------------------

    // How a row's samples are read, so one loop body serves both depths. Code(row, i) is sample i of the row
    // as an integer code; Alpha is sample i as 0..1 (a table lookup at 8 bits; a division at 16, where a
    // 65536-entry alpha table would cost more than it saves and a reciprocal multiply would not be exact).
    private interface ISample
    {
        static abstract int Bytes { get; }
        static abstract int Code(ref byte row, int i);
        static abstract float Alpha(ref byte row, int i, ref float alphaTable);
    }

    private readonly struct Sample8 : ISample
    {
        public static int Bytes => 1;
        public static int Code(ref byte row, int i) => Unsafe.Add(ref row, i);
        public static float Alpha(ref byte row, int i, ref float alphaTable) => Unsafe.Add(ref alphaTable, Unsafe.Add(ref row, i));
    }

    private readonly struct Sample16 : ISample
    {
        public static int Bytes => 2;
        public static int Code(ref byte row, int i) => Read(ref row, i);
        public static float Alpha(ref byte row, int i, ref float alphaTable) => Read(ref row, i) / 65535f;

        // PNG is big-endian.
        private static ushort Read(ref byte row, int i)
        {
            ushort v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref row, i * 2));
            return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(v) : v;
        }
    }

    // The unsafe indexing is in range by construction: a code is at most 2^bits - 1 and the table has 2^bits
    // entries; the row spans below are bounds-checked once per row, and the loops stay inside them.
    private static void FillRows<TSample>(PngImage png, ReadOnlySpan<float> table, ReadOnlySpan<float> alphaTable,
        bool hlgRgb, LumaWeights weights, float[] dst) where TSample : struct, ISample
    {
        int w = png.Width, h = png.Height, spp = png.SamplesPerPixel;
        int colorChannels = png.ColorType is 0 or 4 ? 1 : 3;
        bool hasAlpha = png.ColorType is 4 or 6;
        int rowBytes = w * spp * TSample.Bytes;
        var pixels = png.Pixels;
        ref float t = ref MemoryMarshal.GetReference(table);
        ref float at = ref MemoryMarshal.GetReference(alphaTable);

        for (int y = 0; y < h; y++)
        {
            ref byte p = ref MemoryMarshal.GetReference(pixels.AsSpan(y * rowBytes, rowBytes));
            ref float o = ref MemoryMarshal.GetReference(dst.AsSpan(y * w * 4, w * 4));

            if (colorChannels == 1)
            {
                for (int x = 0; x < w; x++)
                {
                    float v = Unsafe.Add(ref t, TSample.Code(ref p, x * spp));
                    ref float d = ref Unsafe.Add(ref o, x * 4);
                    d = v;
                    Unsafe.Add(ref d, 1) = v;
                    Unsafe.Add(ref d, 2) = v;
                    Unsafe.Add(ref d, 3) = hasAlpha ? TSample.Alpha(ref p, x * spp + 1, ref at) : 1f;
                }
            }
            else if (!hlgRgb)
            {
                for (int x = 0; x < w; x++)
                {
                    int s = x * spp;
                    ref float d = ref Unsafe.Add(ref o, x * 4);
                    d = Unsafe.Add(ref t, TSample.Code(ref p, s));
                    Unsafe.Add(ref d, 1) = Unsafe.Add(ref t, TSample.Code(ref p, s + 1));
                    Unsafe.Add(ref d, 2) = Unsafe.Add(ref t, TSample.Code(ref p, s + 2));
                    Unsafe.Add(ref d, 3) = hasAlpha ? TSample.Alpha(ref p, s + 3, ref at) : 1f;
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    int s = x * spp;
                    ref float d = ref Unsafe.Add(ref o, x * 4);
                    PngHdr.HlgSceneToLinear(
                        Unsafe.Add(ref t, TSample.Code(ref p, s)),
                        Unsafe.Add(ref t, TSample.Code(ref p, s + 1)),
                        Unsafe.Add(ref t, TSample.Code(ref p, s + 2)),
                        weights, out float r, out float g, out float b);
                    d = r;
                    Unsafe.Add(ref d, 1) = g;
                    Unsafe.Add(ref d, 2) = b;
                    Unsafe.Add(ref d, 3) = hasAlpha ? TSample.Alpha(ref p, s + 3, ref at) : 1f;
                }
            }
        }
    }

    // Indexed: the palette is expanded and linearised once, exactly as the reference does, and packed with its
    // tRNS alpha into an RGBA table, so a pixel is one 16-byte copy.
    private static void FillIndexed(PngImage png, ColorEncoding source, float[] dst)
    {
        if (png.Palette is not { } palette)
            throw new InvalidDataException("Indexed PNG has no palette to expand to RGBA");

        int entries = palette.Length / 3;
        var linear = new float[entries * 3];
        PngHdr.ExpandCodes(palette.AsSpan(0, entries * 3), source.FullRange, linear);
        PngHdr.ToLinear(source.Transfer, source.Primaries, linear, 3, 3);

        var paletteAlpha = png.PaletteAlpha;
        var rgba = new float[entries * 4];
        for (int e = 0; e < entries; e++)
        {
            rgba[e * 4] = linear[e * 3];
            rgba[e * 4 + 1] = linear[e * 3 + 1];
            rgba[e * 4 + 2] = linear[e * 3 + 2];
            rgba[e * 4 + 3] = paletteAlpha is not null && e < paletteAlpha.Length ? paletteAlpha[e] / 255f : 1f;
        }

        var pixels = png.Pixels;
        int count = png.Width * png.Height;
        ArgumentOutOfRangeException.ThrowIfLessThan(dst.Length, count * 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, count);
        ref float table = ref MemoryMarshal.GetArrayDataReference(rgba);
        ref float o = ref MemoryMarshal.GetArrayDataReference(dst);
        for (int i = 0; i < count; i++)
        {
            int index = pixels[i];
            if (index >= entries)
                throw new InvalidDataException($"Palette index {index} out of range (palette has {entries} entries)");

            Vector128.LoadUnsafe(ref table, (nuint)(index * 4)).StoreUnsafe(ref o, (nuint)(i * 4));
        }
    }

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
