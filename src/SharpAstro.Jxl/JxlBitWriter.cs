namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL bitstream writer — the inverse of <see cref="JxlBitReader"/>. Bits are emitted
/// LSB-first within each byte (the first-written bit lands in the least-significant position),
/// matching the reader's consumption order (ISO/IEC 18181-1 §C.1).
/// </summary>
internal sealed class JxlBitWriter
{
    private byte[] _buffer = new byte[256];
    private int _byteCount;
    private ulong _acc;    // staged bits not yet flushed; LSB is the next bit to emit
    private int _bitCount; // number of valid staged bits in _acc
    private long _bitsWritten;

    /// <summary>Total number of bits written so far (including byte-alignment padding).</summary>
    public long BitsWritten => _bitsWritten;

    public void WriteBit(bool bit) => WriteBits(bit ? 1u : 0u, 1);

    /// <summary>Writes the low <paramref name="count"/> bits (0..32) of <paramref name="value"/>, LSB-first.</summary>
    public void WriteBits(uint value, int count)
    {
        if (count == 0)
            return;
        ulong masked = count >= 32 ? value : value & ((1u << count) - 1);
        _acc |= masked << _bitCount;
        _bitCount += count;
        _bitsWritten += count;
        while (_bitCount >= 8)
        {
            Append((byte)_acc);
            _acc >>= 8;
            _bitCount -= 8;
        }
    }

    /// <summary>Pads with zero bits up to the next byte boundary (inverse of <see cref="JxlBitReader.ZeroPadToByte"/>).</summary>
    public void ZeroPadToByte()
    {
        if (_bitCount > 0)
        {
            Append((byte)_acc);
            _bitsWritten += 8 - _bitCount;
            _acc = 0;
            _bitCount = 0;
        }
    }

    /// <summary>
    /// Inverse of <see cref="JxlBitReader.ReadU32"/>: writes <paramref name="value"/> using the
    /// lowest-indexed distribution that can represent it (a 2-bit selector then the residual bits).
    /// </summary>
    public void WriteU32(
        uint value,
        (int Offset, int Bits) d0,
        (int Offset, int Bits) d1,
        (int Offset, int Bits) d2,
        (int Offset, int Bits) d3)
    {
        Span<(int Offset, int Bits)> ds = [d0, d1, d2, d3];
        for (uint s = 0; s < 4; s++)
        {
            (int offset, int bits) = ds[(int)s];
            if (value < (uint)offset)
                continue;
            ulong range = bits >= 32 ? 1UL << 32 : 1UL << bits;
            if ((ulong)(value - (uint)offset) < range)
            {
                WriteBits(s, 2);
                WriteBits(value - (uint)offset, bits);
                return;
            }
        }
        throw new InvalidOperationException($"JPEG XL: value {value} not representable by any U32 distribution.");
    }

    /// <summary>Inverse of <see cref="JxlBitReader.ReadEnum"/>.</summary>
    public void WriteEnum(uint value) => WriteU32(value, (0, 0), (1, 0), (2, 4), (18, 6));

    /// <summary>Inverse of <see cref="JxlBitReader.ReadU64"/> — variable-length unsigned, chained 8-bit groups.</summary>
    public void WriteU64(ulong value)
    {
        if (value == 0) { WriteBits(0, 2); return; }
        if (value <= 16) { WriteBits(1, 2); WriteBits((uint)(value - 1), 4); return; }
        if (value <= 272) { WriteBits(2, 2); WriteBits((uint)(value - 17), 8); return; }

        WriteBits(3, 2);
        WriteBits((uint)(value & 0xfff), 12);
        ulong remaining = value >> 12;
        int shift = 12;
        while (remaining != 0)
        {
            WriteBit(true); // continuation
            if (shift < 60)
            {
                WriteBits((uint)(remaining & 0xff), 8);
                remaining >>= 8;
                shift += 8;
            }
            else
            {
                WriteBits((uint)(remaining & 0xf), 4); // final 4-bit group; reader breaks without a terminator
                return;
            }
        }
        WriteBit(false); // terminator
    }

    /// <summary>Appends raw bytes; the writer must be byte-aligned (call <see cref="ZeroPadToByte"/> first).</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (_bitCount != 0)
            throw new InvalidOperationException("JPEG XL: WriteBytes requires a byte-aligned writer.");
        foreach (byte b in bytes)
            Append(b);
        _bitsWritten += (long)bytes.Length * 8;
    }

    /// <summary>Flushes any partial byte (zero-padded) and returns the written bytes.</summary>
    public byte[] ToArray()
    {
        ZeroPadToByte();
        return _buffer.AsSpan(0, _byteCount).ToArray();
    }

    private void Append(byte b)
    {
        if (_byteCount == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);
        _buffer[_byteCount++] = b;
    }
}
