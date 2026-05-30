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
