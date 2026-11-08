namespace SharpAstro.Heif.Hevc;

/// <summary>An HEVC NAL unit: its two-byte header (H.265 7.3.1.2) and its payload as RBSP.</summary>
internal sealed record HevcNalUnit(int Type, int LayerId, int TemporalId, byte[] Rbsp)
{
    public const int VpsNut = 32, SpsNut = 33, PpsNut = 34, AudNut = 35, PrefixSeiNut = 39, SuffixSeiNut = 40;

    /// <summary>IDR_W_RADL..CRA_NUT and the reserved IRAP types: every picture here is one of these (16..23).</summary>
    public bool IsIrap => Type is >= 16 and <= 23;

    /// <summary>A coded slice segment (0..9 and 16..21; 10..15 and 22..31 are reserved VCL types).</summary>
    public bool IsSlice => Type is <= 9 or (>= 16 and <= 21);

    /// <summary>
    /// Parses the header and strips emulation prevention: every <c>00 00 03</c> in the payload loses its
    /// <c>03</c> (H.265 7.4.2). The <c>03</c> exists only so the payload never contains a start code; the
    /// syntax is defined on the bytes without it.
    /// </summary>
    public static HevcNalUnit Parse(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 2) throw new InvalidDataException($"A NAL unit of {nal.Length} bytes is shorter than its header.");
        if ((nal[0] & 0x80) != 0) throw new InvalidDataException("A NAL unit has forbidden_zero_bit set.");
        var type = (nal[0] >> 1) & 0x3F;
        var layer = ((nal[0] & 1) << 5) | (nal[1] >> 3);
        var temporalPlus1 = nal[1] & 7;
        if (temporalPlus1 == 0) throw new InvalidDataException("A NAL unit has nuh_temporal_id_plus1 = 0.");
        return new HevcNalUnit(type, layer, temporalPlus1 - 1, StripEmulationPrevention(nal[2..]));
    }

    public static byte[] StripEmulationPrevention(ReadOnlySpan<byte> payload)
    {
        var rbsp = new byte[payload.Length];
        var length = 0;
        var zeros = 0;
        foreach (var b in payload)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }
            rbsp[length++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return length == rbsp.Length ? rbsp : rbsp[..length];
    }

    /// <summary>
    /// Splits HEIF item data, a sequence of NAL units each preceded by a big-endian length of
    /// <paramref name="lengthSize"/> bytes (14496-15 / 23008-12), into the NAL units themselves.
    /// </summary>
    public static List<byte[]> SplitLengthPrefixed(ReadOnlySpan<byte> data, int lengthSize)
    {
        var nals = new List<byte[]>();
        var position = 0;
        while (position < data.Length)
        {
            if (data.Length - position < lengthSize)
                throw new InvalidDataException($"The item data ends inside a NAL unit length at {position}.");
            long length = 0;
            for (var i = 0; i < lengthSize; i++) length = length << 8 | data[position + i];
            position += lengthSize;
            if (length > data.Length - position)
                throw new InvalidDataException($"A NAL unit at {position} declares {length} bytes, {length - (data.Length - position)} past the end of the item.");
            nals.Add(data.Slice(position, (int)length).ToArray());
            position += (int)length;
        }
        return nals;
    }
}

/// <summary>
/// Reads RBSP syntax elements (H.265 7.2, 9.2): fixed-width <c>u(n)</c> MSB first, and the Exp-Golomb
/// <c>ue(v)</c> / <c>se(v)</c>. Reading past the end throws, since a parameter set that runs out of bits
/// is corrupt, and treating the missing bits as zeros would quietly invent values.
/// </summary>
internal ref struct RbspReader
{
    private readonly ReadOnlySpan<byte> _data;
    private long _bit;

    public RbspReader(ReadOnlySpan<byte> rbsp) => _data = rbsp;

    public readonly long BitPosition => _bit;
    public readonly long BitsLeft => _data.Length * 8L - _bit;
    public readonly bool ByteAligned => (_bit & 7) == 0;

    public bool Flag() => U(1) != 0;

    public uint U(int bits)
    {
        if (bits is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        if (bits > BitsLeft) throw new InvalidDataException($"The RBSP ends {bits - BitsLeft} bits short of a {bits}-bit field.");
        ulong value = 0;
        for (var i = 0; i < bits; i++)
        {
            var b = _data[(int)(_bit >> 3)] >> (7 - (int)(_bit & 7)) & 1;
            value = value << 1 | (uint)b;
            _bit++;
        }
        return (uint)value;
    }

    /// <summary>ue(v). Values above 2^32 - 2 are refused: no HEVC syntax element is that large.</summary>
    public uint Ue()
    {
        var zeros = 0;
        while (U(1) == 0)
        {
            if (++zeros > 31) throw new InvalidDataException("An Exp-Golomb code has more than 31 leading zeros.");
        }
        if (zeros == 0) return 0;
        var value = (1UL << zeros) - 1 + U(zeros);
        if (value > uint.MaxValue - 1) throw new InvalidDataException("An Exp-Golomb value overflows 32 bits.");
        return (uint)value;
    }

    /// <summary>ue(v) bounded to <c>[0, max]</c>, as the semantics of nearly every element require.</summary>
    public int Ue(int max, string name)
    {
        var value = Ue();
        if (value > (uint)max) throw new InvalidDataException($"{name} = {value} exceeds its maximum of {max}.");
        return (int)value;
    }

    public int Se()
    {
        var k = Ue();
        return (k & 1) != 0 ? (int)((k + 1) >> 1) : -(int)(k >> 1);
    }

    public int Se(int min, int max, string name)
    {
        var value = Se();
        if (value < min || value > max) throw new InvalidDataException($"{name} = {value} is outside [{min}, {max}].");
        return value;
    }

    public void Skip(int bits)
    {
        if (bits > BitsLeft) throw new InvalidDataException("The RBSP ends inside skipped bits.");
        _bit += bits;
    }

    /// <summary>
    /// more_rbsp_data() (7.2): true while anything other than the rbsp_stop_one_bit and its trailing zero
    /// bits remains.
    /// </summary>
    public readonly bool MoreRbspData()
    {
        if (BitsLeft <= 0) return false;
        var last = _data.Length - 1;
        while (last >= 0 && _data[last] == 0) last--;
        if (last < 0) return false;
        var stopBit = last * 8L + 7 - System.Numerics.BitOperations.TrailingZeroCount(_data[last]);
        return _bit < stopBit;
    }
}
