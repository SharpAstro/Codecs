using System.Buffers.Binary;

namespace SharpAstro.Exr;

/// <summary>
/// PIZ compression (a faithful port of OpenEXRCore's internal_piz.c): the block's
/// samples are reorganized channel-major as 16-bit words, range-reduced through a
/// bitmap+LUT, transformed by a 2D Haar-like wavelet per channel, and Huffman-coded
/// (<see cref="ExrHuffman"/>). FLOAT channels are treated as two interleaved 16-bit
/// planes (low/high half of each float), each wavelet-transformed independently.
/// Lossless. Stream layout: minNZ(u16) maxNZ(u16) bitmap[min..max] hufLen(u32) hufData.
/// </summary>
internal static class ExrPiz
{
    private const int UShortRange = 1 << 16;
    private const int BitmapSize = UShortRange >> 3; // 8192

    public static byte[] Compress(byte[] raw, ExrBlockInfo info)
    {
        int nx = info.Width, ny = info.ScanlineCount;
        var channels = info.Channels;
        int ndata = raw.Length / 2;
        var data = new ushort[ndata];

        // Reorganize scanline-major raw bytes -> channel-major 16-bit words.
        ReorgToChannelMajor(raw, data, nx, ny, channels, toWords: true);

        // Range compression: bitmap of used values, then a compacting LUT.
        var bitmap = new byte[BitmapSize];
        BitmapFromData(data, ndata, bitmap, out ushort minNZ, out ushort maxNZ);
        var lut = new ushort[UShortRange];
        ushort maxValue = ForwardLutFromBitmap(bitmap, lut);
        ApplyLut(lut, data, ndata);

        // 2D wavelet per channel, per 16-bit sub-plane.
        WaveletAllChannels(data, nx, ny, channels, maxValue, encode: true);

        // Huffman-compress the whole (lut-applied, wavelet-transformed) word array.
        byte[] huf = ExrHuffman.Compress(data);

        var outp = new byte[4 + (minNZ <= maxNZ ? maxNZ - minNZ + 1 : 0) + 4 + huf.Length];
        int o = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(outp.AsSpan(o), minNZ); o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(outp.AsSpan(o), maxNZ); o += 2;
        if (minNZ <= maxNZ)
        {
            int bpl = maxNZ - minNZ + 1;
            bitmap.AsSpan(minNZ, bpl).CopyTo(outp.AsSpan(o)); o += bpl;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(o), (uint)huf.Length); o += 4;
        huf.CopyTo(outp.AsSpan(o));
        return outp;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize, ExrBlockInfo info)
    {
        int nx = info.Width, ny = info.ScanlineCount;
        var channels = info.Channels;
        int ndata = uncompressedSize / 2;

        int p = 0;
        ushort minNZ = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(p)); p += 2;
        ushort maxNZ = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(p)); p += 2;
        if (maxNZ >= BitmapSize) throw new InvalidDataException("Corrupt EXR PIZ bitmap range.");

        var bitmap = new byte[BitmapSize];
        if (minNZ <= maxNZ)
        {
            int bpl = maxNZ - minNZ + 1;
            src.Slice(p, bpl).CopyTo(bitmap.AsSpan(minNZ)); p += bpl;
        }
        var lut = new ushort[UShortRange];
        ushort maxValue = ReverseLutFromBitmap(bitmap, lut);

        int hufLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(p)); p += 4;
        var data = ExrHuffman.Decompress(src.Slice(p, hufLen), ndata);

        WaveletAllChannels(data, nx, ny, channels, maxValue, encode: false);
        ApplyLut(lut, data, ndata);

        var raw = new byte[uncompressedSize];
        ReorgFromChannelMajor(data, raw, nx, ny, channels);
        return raw;
    }

    // ----------------------------------------------------------------- reorg

    private static void ReorgToChannelMajor(byte[] raw, ushort[] data, int nx, int ny, ExrChannel[] channels, bool toWords)
    {
        int rowBytes = 0;
        foreach (var c in channels) rowBytes += nx * c.BytesPerSample;

        int channelBaseWords = 0, colOffsetBytes = 0;
        foreach (var c in channels)
        {
            int wcount = c.BytesPerSample / 2;
            int rowWords = nx * wcount;
            int chanBytes = nx * c.BytesPerSample;
            for (var s = 0; s < ny; s++)
            {
                int srcOff = s * rowBytes + colOffsetBytes;
                int dstOff = channelBaseWords + s * rowWords;
                for (var w = 0; w < rowWords; w++)
                    data[dstOff + w] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(srcOff + w * 2));
            }
            channelBaseWords += rowWords * ny;
            colOffsetBytes += chanBytes;
        }
    }

    private static void ReorgFromChannelMajor(ushort[] data, byte[] raw, int nx, int ny, ExrChannel[] channels)
    {
        int rowBytes = 0;
        foreach (var c in channels) rowBytes += nx * c.BytesPerSample;

        int channelBaseWords = 0, colOffsetBytes = 0;
        foreach (var c in channels)
        {
            int wcount = c.BytesPerSample / 2;
            int rowWords = nx * wcount;
            int chanBytes = nx * c.BytesPerSample;
            for (var s = 0; s < ny; s++)
            {
                int dstOff = s * rowBytes + colOffsetBytes;
                int srcOff = channelBaseWords + s * rowWords;
                for (var w = 0; w < rowWords; w++)
                    BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(dstOff + w * 2), data[srcOff + w]);
            }
            channelBaseWords += rowWords * ny;
            colOffsetBytes += chanBytes;
        }
    }

    // ----------------------------------------------------------------- bitmap / LUT

    private static void BitmapFromData(ushort[] data, int n, byte[] bitmap, out ushort minNZ, out ushort maxNZ)
    {
        Array.Clear(bitmap);
        for (var i = 0; i < n; i++) bitmap[data[i] >> 3] |= (byte)(1 << (data[i] & 7));
        bitmap[0] &= 0xFE; // zero is implicit

        int mn = BitmapSize - 1, mx = 0;
        for (var i = 0; i < BitmapSize; i++)
            if (bitmap[i] != 0) { if (mn > i) mn = i; if (mx < i) mx = i; }
        minNZ = (ushort)mn; maxNZ = (ushort)mx;
    }

    private static ushort ForwardLutFromBitmap(byte[] bitmap, ushort[] lut)
    {
        int k = 0;
        for (var i = 0; i < UShortRange; i++)
        {
            if (i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0) lut[i] = (ushort)(k++);
            else lut[i] = 0;
        }
        return (ushort)(k - 1);
    }

    private static ushort ReverseLutFromBitmap(byte[] bitmap, ushort[] lut)
    {
        int k = 0;
        for (var i = 0; i < UShortRange; i++)
            if (i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0) lut[k++] = (ushort)i;
        int n = k - 1;
        while (k < UShortRange) lut[k++] = 0;
        return (ushort)n;
    }

    private static void ApplyLut(ushort[] lut, ushort[] data, int n)
    {
        for (var i = 0; i < n; i++) data[i] = lut[data[i]];
    }

    // ----------------------------------------------------------------- wavelet driver

    private static void WaveletAllChannels(ushort[] data, int nx, int ny, ExrChannel[] channels, ushort maxValue, bool encode)
    {
        int channelBaseWords = 0;
        foreach (var c in channels)
        {
            int wcount = c.BytesPerSample / 2;
            for (var j = 0; j < wcount; j++)
            {
                if (encode) Wav2DEncode(data, channelBaseWords + j, nx, wcount, ny, wcount * nx, maxValue);
                else Wav2DDecode(data, channelBaseWords + j, nx, wcount, ny, wcount * nx, maxValue);
            }
            channelBaseWords += nx * ny * wcount;
        }
    }

    // ----------------------------------------------------------------- wavelet basis

    private static void Wenc14(ushort a, ushort b, out ushort l, out ushort h)
    {
        short as_ = (short)a, bs = (short)b;
        l = (ushort)(short)((as_ + bs) >> 1);
        h = (ushort)(short)(as_ - bs);
    }

    private static void Wdec14(ushort l, ushort h, out ushort a, out ushort b)
    {
        int hi = (short)h, li = (short)l;
        int ai = li + (hi & 1) + (hi >> 1);
        a = (ushort)(short)ai;
        b = (ushort)(short)(ai - hi);
    }

    private static void Wdec14_4(ushort[] d, int px, int p01, int p10, int p11)
    {
        int ai = (short)d[px], bi = (short)d[p10], ci = (short)d[p01], di = (short)d[p11];
        int i00 = ai + (bi & 1) + (bi >> 1);
        int i10 = i00 - bi;
        int i01 = ci + (di & 1) + (di >> 1);
        int i11 = i01 - di;
        ai = i00 + (i01 & 1) + (i01 >> 1);
        bi = ai - i01;
        ci = i10 + (i11 & 1) + (i11 >> 1);
        di = ci - i11;
        d[px] = (ushort)ai; d[p01] = (ushort)bi; d[p10] = (ushort)ci; d[p11] = (ushort)di;
    }

    private const int AOffset = 1 << 15, MOffset = 1 << 15, ModMask = (1 << 16) - 1;

    private static void Wenc16(ushort a, ushort b, out ushort l, out ushort h)
    {
        int ao = (a + AOffset) & ModMask;
        int m = (ao + b) >> 1;
        int d = ao - b;
        if (d < 0) m = (m + MOffset) & ModMask;
        d &= ModMask;
        l = (ushort)m; h = (ushort)d;
    }

    private static void Wdec16(ushort l, ushort h, out ushort a, out ushort b)
    {
        int m = l, d = h;
        int bb = (m - (d >> 1)) & ModMask;
        int aa = (d + bb - AOffset) & ModMask;
        b = (ushort)bb; a = (ushort)aa;
    }

    private static void Wav2DEncode(ushort[] inb, int baseIdx, int nx, int ox, int ny, int oy, ushort mx)
    {
        bool w14 = mx < (1 << 14);
        int n = nx > ny ? ny : nx;
        int p = 1, p2 = 2;
        long oy64 = oy;

        while (p2 <= n)
        {
            int py = baseIdx;
            int ey = baseIdx + (int)(oy64 * (ny - p2));
            int oy1 = (int)(oy64 * p), oy2 = (int)(oy64 * p2);
            int ox1 = ox * p, ox2 = ox * p2;

            for (; py <= ey; py += oy2)
            {
                int px = py;
                int ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1, p10 = px + oy1, p11 = p10 + ox1;
                    if (w14)
                    {
                        Wenc14(inb[px], inb[p01], out ushort i00, out ushort i01);
                        Wenc14(inb[p10], inb[p11], out ushort i10, out ushort i11);
                        Wenc14(i00, i10, out inb[px], out inb[p10]);
                        Wenc14(i01, i11, out inb[p01], out inb[p11]);
                    }
                    else
                    {
                        Wenc16(inb[px], inb[p01], out ushort i00, out ushort i01);
                        Wenc16(inb[p10], inb[p11], out ushort i10, out ushort i11);
                        Wenc16(i00, i10, out inb[px], out inb[p10]);
                        Wenc16(i01, i11, out inb[p01], out inb[p11]);
                    }
                }
                if ((nx & p) != 0)
                {
                    int p10 = px + oy1;
                    if (w14) Wenc14(inb[px], inb[p10], out inb[px], out inb[p10]);
                    else Wenc16(inb[px], inb[p10], out inb[px], out inb[p10]);
                }
            }

            if ((ny & p) != 0)
            {
                int px = py;
                int ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1;
                    if (w14) Wenc14(inb[px], inb[p01], out inb[px], out inb[p01]);
                    else Wenc16(inb[px], inb[p01], out inb[px], out inb[p01]);
                }
            }

            p = p2; p2 <<= 1;
        }
    }

    private static void Wav2DDecode(ushort[] inb, int baseIdx, int nx, int ox, int ny, int oy, ushort mx)
    {
        bool w14 = mx < (1 << 14);
        int n = nx > ny ? ny : nx;
        int p = 1, p2;
        long oy64 = oy;

        while (p <= n) p <<= 1;
        p >>= 1; p2 = p; p >>= 1;

        while (p >= 1)
        {
            int py = baseIdx;
            int ey = baseIdx + (int)(oy64 * (ny - p2));
            int oy1 = (int)(oy64 * p), oy2 = (int)(oy64 * p2);
            int ox1 = ox * p, ox2 = ox * p2;

            for (; py <= ey; py += oy2)
            {
                int px = py;
                int ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1, p10 = px + oy1, p11 = p10 + ox1;
                    if (w14)
                    {
                        Wdec14_4(inb, px, p01, p10, p11);
                    }
                    else
                    {
                        Wdec16(inb[px], inb[p10], out ushort i00, out ushort i10);
                        Wdec16(inb[p01], inb[p11], out ushort i01, out ushort i11);
                        Wdec16(i00, i01, out inb[px], out inb[p01]);
                        Wdec16(i10, i11, out inb[p10], out inb[p11]);
                    }
                }
                if ((nx & p) != 0)
                {
                    int p10 = px + oy1;
                    ushort i00;
                    if (w14) Wdec14(inb[px], inb[p10], out i00, out inb[p10]);
                    else Wdec16(inb[px], inb[p10], out i00, out inb[p10]);
                    inb[px] = i00;
                }
            }

            if ((ny & p) != 0)
            {
                int px = py;
                int ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1;
                    ushort i00;
                    if (w14) Wdec14(inb[px], inb[p01], out i00, out inb[p01]);
                    else Wdec16(inb[px], inb[p01], out i00, out inb[p01]);
                    inb[px] = i00;
                }
            }

            p2 = p; p >>= 1;
        }
    }
}
