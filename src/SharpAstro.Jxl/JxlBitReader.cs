namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL bitstream reader (ISO/IEC 18181-1 §C.1). Bits are consumed LSB-first within
/// each byte, bytes in order; every multi-bit read returns the first-read bit in the
/// least-significant position.
/// </summary>
internal ref struct JxlBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bytePos;
    private ulong _buffer; // staged bits; LSB is the next bit to consume
    private int _bitCount; // number of valid bits currently in _buffer
    private long _bitsRead; // total bits consumed (for byte alignment / accounting)

    public JxlBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bytePos = 0;
        _buffer = 0;
        _bitCount = 0;
        _bitsRead = 0;
    }

    private void Refill()
    {
        while (_bitCount <= 56 && _bytePos < _data.Length)
        {
            _buffer |= (ulong)_data[_bytePos++] << _bitCount;
            _bitCount += 8;
        }
    }

    /// <summary>Reads <paramref name="count"/> bits (0..32), LSB-first.</summary>
    public uint ReadBits(int count)
    {
        if (count == 0)
            return 0;
        if (_bitCount < count)
            Refill();
        uint value = (uint)(_buffer & ((1UL << count) - 1));
        _buffer >>= count;
        _bitCount -= count;
        _bitsRead += count;
        return value;
    }

    public bool ReadBit() => ReadBits(1) != 0;

    /// <summary>
    /// Peeks the next <paramref name="count"/> bits (0..32) without consuming them; bits past
    /// end-of-stream read as zero. Pairs with <see cref="ConsumeBits"/> for entropy decoding,
    /// where the number of bits inspected and the number actually consumed differ.
    /// </summary>
    public uint PeekBits(int count)
    {
        if (count == 0)
            return 0;
        if (_bitCount < count)
            Refill();
        return (uint)(_buffer & ((1UL << count) - 1));
    }

    /// <summary>Advances past <paramref name="count"/> bits previously inspected via <see cref="PeekBits"/>.</summary>
    public void ConsumeBits(int count)
    {
        if (count == 0)
            return;
        if (_bitCount < count)
            Refill();
        int avail = _bitCount < count ? _bitCount : count; // clamp at EOF so _bitCount never goes negative
        _buffer >>= avail;
        _bitCount -= avail;
        _bitsRead += count;
    }

    /// <summary>Total number of bits consumed so far.</summary>
    public long BitsRead => _bitsRead;

    /// <summary>Bytes consumed so far (exact only when byte-aligned, e.g. after ZeroPadToByte).</summary>
    public long BytesRead => _bitsRead / 8;

    /// <summary>Discards bits up to the next byte boundary (JPEG XL zero_pad_to_byte, §C.1).</summary>
    public void ZeroPadToByte()
    {
        int remainder = (int)(_bitsRead & 7);
        if (remainder != 0)
            ReadBits(8 - remainder);
    }

    /// <summary>
    /// JPEG XL U32(d0,d1,d2,d3) (§C.1): a 2-bit selector picks one of four distributions,
    /// each supplied here as (offset, bits): the decoded value is offset + ReadBits(bits).
    /// </summary>
    public uint ReadU32(
        (int Offset, int Bits) d0,
        (int Offset, int Bits) d1,
        (int Offset, int Bits) d2,
        (int Offset, int Bits) d3)
    {
        uint selector = ReadBits(2);
        (int Offset, int Bits) d = selector switch
        {
            0 => d0,
            1 => d1,
            2 => d2,
            _ => d3,
        };
        return (uint)d.Offset + ReadBits(d.Bits);
    }

    /// <summary>
    /// JPEG XL Enum() (§C.1): U32(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(6, 18)).
    /// </summary>
    public uint ReadEnum() => ReadU32((0, 0), (1, 0), (2, 4), (18, 6));

    /// <summary>JPEG XL U64() (§C.1) — variable-length unsigned, chained 8-bit groups.</summary>
    public ulong ReadU64()
    {
        uint selector = ReadBits(2);
        switch (selector)
        {
            case 0:
                return 0;
            case 1:
                return 1 + ReadBits(4);
            case 2:
                return 17 + ReadBits(8);
            default:
                ulong value = ReadBits(12);
                int shift = 12;
                while (ReadBit())
                {
                    if (shift < 60)
                    {
                        value |= (ulong)ReadBits(8) << shift;
                        shift += 8;
                    }
                    else
                    {
                        value |= (ulong)ReadBits(4) << 60;
                        break;
                    }
                }
                return value;
        }
    }
}
