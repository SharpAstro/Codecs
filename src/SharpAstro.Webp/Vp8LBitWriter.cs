namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 (WebP Lossless Bitstream
// Specification) only. No libwebp, libvpx or RFC 6386 section 20 source was read or transcribed;
// cwebp and dwebp are used only as black-box test oracles.

/// <summary>
/// The VP8L bit writer: RFC 9649 section 3.2 packs values least-significant bit first, so each
/// value's low bit lands in the lowest free bit of the current byte. This is the mirror of the
/// decoder's bit reader.
/// </summary>
internal sealed class Vp8LBitWriter
{
    private byte[] _buffer;
    private int _length;
    private ulong _accumulator;
    private int _bits;

    public Vp8LBitWriter(int initialCapacity)
    {
        _buffer = new byte[Math.Max(initialCapacity, 16)];
    }

    /// <summary>The number of bits written so far.</summary>
    public long BitCount => (long)_length * 8 + _bits;

    /// <summary>Writes the low <paramref name="count"/> bits (0 to 32) of <paramref name="value"/>.</summary>
    public void WriteBits(uint value, int count)
    {
        if ((uint)count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0)
        {
            return;
        }

        ulong masked = count == 32 ? value : value & ((1UL << count) - 1);
        _accumulator |= masked << _bits;
        _bits += count;
        if (_bits >= 32)
        {
            Ensure(4);
            _buffer[_length++] = (byte)_accumulator;
            _buffer[_length++] = (byte)(_accumulator >> 8);
            _buffer[_length++] = (byte)(_accumulator >> 16);
            _buffer[_length++] = (byte)(_accumulator >> 24);
            _accumulator >>= 32;
            _bits -= 32;
        }
    }

    /// <summary>Pads the last partial byte with zero bits and returns the bytes written.</summary>
    public ReadOnlySpan<byte> Finish()
    {
        Ensure(4);
        while (_bits > 0)
        {
            _buffer[_length++] = (byte)_accumulator;
            _accumulator >>= 8;
            _bits = Math.Max(0, _bits - 8);
        }

        _accumulator = 0;
        return _buffer.AsSpan(0, _length);
    }

    private void Ensure(int extra)
    {
        if (_length + extra > _buffer.Length)
        {
            Array.Resize(ref _buffer, (int)Math.Min(Array.MaxLength, Math.Max((long)_buffer.Length * 2, (long)_length + extra)));
        }
    }
}
