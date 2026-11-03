using System.Buffers.Binary;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Builds BMP files by hand, field by field, for everything ImageMagick cannot write: top-down rows,
/// 8-bit without RLE, RLE4, 16-bit 5-5-5 BI_RGB, odd mask layouts, V5 ICC profiles, and deliberately
/// broken headers. The layout is file header | DIB header | masks after a 40-byte header | palette |
/// pixels | ICC profile.
/// </summary>
internal sealed class BmpDib
{
    public int HeaderSize = 40;
    public int Width = 1;

    /// <summary>Signed: negative means top-down.</summary>
    public int Height = 1;
    public int BitsPerPixel = 24;
    public uint Compression;
    public uint ClrUsed;

    /// <summary>Palette bytes exactly as stored: 4-byte BGRx entries, or 3-byte BGR for header size 12.</summary>
    public byte[] Palette = [];
    public byte[] Pixels = [];

    /// <summary>Red, green, blue and alpha masks: inside the header from 52 bytes on, after it at 40.</summary>
    public uint[]? Masks;

    /// <summary>With a 40-byte header and masks: write the alpha mask too (BI_ALPHABITFIELDS).</summary>
    public bool FourMasksAfterHeader;
    public byte[]? IccProfile;
    public uint ColorSpaceType;

    public uint? ProfileOffsetOverride;
    public uint? ProfileSizeOverride;
    public uint? PixelOffsetOverride;

    public byte[] Build()
    {
        int maskExtra = HeaderSize == 40 && Masks is not null ? (FourMasksAfterHeader ? 16 : 12) : 0;
        int offBits = 14 + HeaderSize + maskExtra + Palette.Length;
        int total = offBits + Pixels.Length + (IccProfile?.Length ?? 0);

        var f = new byte[total];
        f[0] = (byte)'B';
        f[1] = (byte)'M';
        W32(f, 2, (uint)total);
        W32(f, 10, PixelOffsetOverride ?? (uint)offBits);
        W32(f, 14, (uint)HeaderSize);

        int h = 14;
        if (HeaderSize == 12)
        {
            W16(f, h + 4, (ushort)Width);
            W16(f, h + 6, (ushort)Height);
            W16(f, h + 8, 1);
            W16(f, h + 10, (ushort)BitsPerPixel);
        }
        else
        {
            W32(f, h + 4, (uint)Width);
            W32(f, h + 8, (uint)Height);
            W16(f, h + 12, 1);
            W16(f, h + 14, (ushort)BitsPerPixel);
            if (HeaderSize >= 20) W32(f, h + 16, Compression);
            if (HeaderSize >= 24) W32(f, h + 20, (uint)Pixels.Length);
            if (HeaderSize >= 36) W32(f, h + 32, ClrUsed);
            if (HeaderSize >= 52 && Masks is not null)
            {
                for (int i = 0; i < Math.Min(Masks.Length, HeaderSize >= 56 ? 4 : 3); i++)
                    W32(f, h + 40 + i * 4, Masks[i]);
            }
            if (HeaderSize >= 124 || HeaderSize == 108)
                W32(f, h + 56, ColorSpaceType != 0 ? ColorSpaceType : IccProfile is not null ? 0x4D424544u : 0x73524742u);
            if (HeaderSize == 124 && IccProfile is not null)
            {
                W32(f, h + 112, ProfileOffsetOverride ?? (uint)(HeaderSize + maskExtra + Palette.Length + Pixels.Length));
                W32(f, h + 116, ProfileSizeOverride ?? (uint)IccProfile.Length);
            }
        }

        if (maskExtra > 0)
            for (int i = 0; i < maskExtra / 4; i++)
                W32(f, h + HeaderSize + i * 4, i < Masks!.Length ? Masks[i] : 0);

        Palette.CopyTo(f, offBits - Palette.Length);
        Pixels.CopyTo(f, offBits);
        IccProfile?.CopyTo(f, offBits + Pixels.Length);
        return f;
    }

    /// <summary>
    /// Packs uncompressed rows, padded to four bytes. <paramref name="pixelAt"/> gets (x, y) with y
    /// counted from the top of the picture and returns a palette index or the pixel's little-endian bits.
    /// A bottom-up file stores the picture's last row first.
    /// </summary>
    public static byte[] PackRows(int width, int height, int bpp, bool topDown, Func<int, int, uint> pixelAt)
    {
        int stride = (width * bpp + 31) / 32 * 4;
        var data = new byte[stride * height];
        for (int fileRow = 0; fileRow < height; fileRow++)
        {
            int y = topDown ? fileRow : height - 1 - fileRow;
            var row = data.AsSpan(fileRow * stride, stride);
            for (int x = 0; x < width; x++)
            {
                uint v = pixelAt(x, y);
                switch (bpp)
                {
                    case 1: row[x >> 3] |= (byte)((v & 1) << (7 - (x & 7))); break;
                    case 4: row[x >> 1] |= (byte)((x & 1) == 0 ? (v & 15) << 4 : v & 15); break;
                    case 8: row[x] = (byte)v; break;
                    case 16: BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)v); break;
                    case 24: row[x * 3] = (byte)v; row[x * 3 + 1] = (byte)(v >> 8); row[x * 3 + 2] = (byte)(v >> 16); break;
                    case 32: BinaryPrimitives.WriteUInt32LittleEndian(row[(x * 4)..], v); break;
                    default: throw new ArgumentOutOfRangeException(nameof(bpp));
                }
            }
        }
        return data;
    }

    /// <summary>A palette of <paramref name="entries"/> distinct, well-spread colours as BGRx (or BGR for core headers).</summary>
    public static byte[] MakePalette(int entries, bool core = false, int seed = 1)
    {
        int size = core ? 3 : 4;
        var p = new byte[entries * size];
        for (int i = 0; i < entries; i++)
        {
            p[i * size] = (byte)(i * 53 + seed * 17 + 200);      // B
            p[i * size + 1] = (byte)(i * 91 + seed * 5 + 5);     // G
            p[i * size + 2] = (byte)(i * 37 + seed * 11 + 11);   // R
        }
        return p;
    }

    private static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
}
