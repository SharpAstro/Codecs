namespace SharpAstro.Bmp;

/// <summary>The uncompressed pixel layouts: indexed 1/4/8, 16 and 32 bit with masks or defaults, 24 bit.</summary>
internal static class BmpRaster
{
    /// <summary>The palette as 256 RGB triples; entries the file does not carry are black.</summary>
    internal static byte[] ReadPalette(ReadOnlySpan<byte> d, BmpHeader h)
    {
        var palette = new byte[256 * 3];
        int entry = h.PaletteEntrySize;
        for (int i = 0; i < h.PaletteCount; i++)
        {
            var e = d.Slice(h.PaletteOffset + i * entry, entry);
            palette[i * 3] = e[2];
            palette[i * 3 + 1] = e[1];
            palette[i * 3 + 2] = e[0];
        }
        return palette;
    }

    internal static void Decode(ReadOnlySpan<byte> d, BmpHeader h, byte[] dst)
    {
        int width = h.Width, height = h.Height, channels = h.Info.Channels;
        int stride = checked((int)h.Stride);
        int bpp = h.BitsPerPixel;
        bool topDown = h.Info.TopDown;
        int dstRowBytes = width * channels;

        byte[] palette = bpp <= 8 ? ReadPalette(d, h) : [];
        MaskChannel red = default, green = default, blue = default, alpha = default;
        if (h.UsesMasks || bpp == 16)
        {
            red = new MaskChannel(h.RedMask);
            green = new MaskChannel(h.GreenMask);
            blue = new MaskChannel(h.BlueMask);
            alpha = new MaskChannel(h.AlphaMask);
        }

        for (int y = 0; y < height; y++)
        {
            int fileRow = topDown ? y : height - 1 - y;
            var src = d.Slice(h.PixelOffset + fileRow * stride, stride);
            var row = dst.AsSpan(y * dstRowBytes, dstRowBytes);

            switch (bpp)
            {
                case 1:
                    for (int x = 0, o = 0; x < width; x++, o += 3)
                    {
                        int index = (src[x >> 3] >> (7 - (x & 7))) & 1;
                        palette.AsSpan(index * 3, 3).CopyTo(row[o..]);
                    }
                    break;
                case 4:
                    for (int x = 0, o = 0; x < width; x++, o += 3)
                    {
                        int b = src[x >> 1];
                        int index = (x & 1) == 0 ? b >> 4 : b & 15;
                        palette.AsSpan(index * 3, 3).CopyTo(row[o..]);
                    }
                    break;
                case 8:
                    for (int x = 0, o = 0; x < width; x++, o += 3)
                        palette.AsSpan(src[x] * 3, 3).CopyTo(row[o..]);
                    break;
                case 24:
                    for (int x = 0, s = 0; x < width; x++, s += 3)
                    {
                        row[s] = src[s + 2];
                        row[s + 1] = src[s + 1];
                        row[s + 2] = src[s];
                    }
                    break;
                case 16:
                    for (int x = 0, o = 0; x < width; x++, o += channels)
                    {
                        uint p = (uint)(src[x * 2] | src[x * 2 + 1] << 8);
                        row[o] = red.Scale(p);
                        row[o + 1] = green.Scale(p);
                        row[o + 2] = blue.Scale(p);
                        if (channels == 4) row[o + 3] = alpha.Scale(p);
                    }
                    break;
                default: // 32
                    if (h.UsesMasks)
                    {
                        for (int x = 0, o = 0; x < width; x++, o += channels)
                        {
                            uint p = (uint)(src[x * 4] | src[x * 4 + 1] << 8 | src[x * 4 + 2] << 16 | src[x * 4 + 3] << 24);
                            row[o] = red.Scale(p);
                            row[o + 1] = green.Scale(p);
                            row[o + 2] = blue.Scale(p);
                            if (channels == 4) row[o + 3] = alpha.Scale(p);
                        }
                    }
                    else
                    {
                        for (int x = 0, s = 0, o = 0; x < width; x++, s += 4, o += channels)
                        {
                            row[o] = src[s + 2];
                            row[o + 1] = src[s + 1];
                            row[o + 2] = src[s];
                            if (channels == 4) row[o + 3] = src[s + 3];
                        }
                    }
                    break;
            }
        }

        if (h.Rgb32HasAlpha)
            OpaqueIfNoAlpha(dst);
    }

    /// <summary>
    /// 32-bit BI_RGB files from X8R8G8B8 writers leave the fourth byte zero everywhere, which read as
    /// alpha would be a fully transparent picture. All zeros therefore means "no alpha" (stb_image does
    /// the same); any other file is read as it is stored.
    /// </summary>
    private static void OpaqueIfNoAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 0) return;
        for (int i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
    }

    /// <summary>One colour channel of a masked 16/32-bit pixel, scaled to 8 bits by rounding to nearest.</summary>
    private readonly struct MaskChannel
    {
        private readonly uint _mask;
        private readonly int _shift;
        private readonly uint _max;
        private readonly byte[]? _lut;

        public MaskChannel(uint mask)
        {
            _mask = mask;
            if (mask == 0)
            {
                _shift = 0;
                _max = 0;
                _lut = null;
                return;
            }
            _shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
            _max = mask >> _shift;
            if (_max <= 0xFFFF)
            {
                // Up to 16 bits a table is cheaper than the division and covers every 5/6-bit layout.
                var lut = new byte[_max + 1];
                for (uint v = 0; v <= _max; v++)
                    lut[v] = ScaleValue(v, _max);
                _lut = lut;
            }
            else
            {
                _lut = null;
            }
        }

        public byte Scale(uint pixel)
        {
            if (_mask == 0) return 0;
            uint v = (pixel & _mask) >> _shift;
            return _lut is not null ? _lut[v] : ScaleValue(v, _max);
        }

        private static byte ScaleValue(uint v, uint max) =>
            (byte)(((ulong)v * 255 + max / 2) / max);
    }
}
