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

    public JxlBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bytePos = 0;
        _buffer = 0;
        _bitCount = 0;
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
        return value;
    }

    public bool ReadBit() => ReadBits(1) != 0;

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
}
