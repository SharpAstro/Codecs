namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Writes HEVC RBSP syntax (H.265 7.2, 9.2) for tests: <c>u(n)</c>, <c>ue(v)</c>, <c>se(v)</c>, the
/// trailing bits, and the NAL header plus emulation prevention, so parameter sets can be built field by
/// field and handed to the parser through the same path as a real stream.
/// </summary>
internal sealed class HevcBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitsInLast = 8;

    public HevcBitWriter U(int bits, long value)
    {
        for (var i = bits - 1; i >= 0; i--) Bit((int)(value >> i & 1));
        return this;
    }

    public HevcBitWriter Flag(bool value) => U(1, value ? 1 : 0);

    public HevcBitWriter Ue(long value)
    {
        var code = value + 1;
        var length = 0;
        while (code >> (length + 1) != 0) length++;
        U(length, 0);
        return U(length + 1, code);
    }

    public HevcBitWriter Se(long value) => Ue(value > 0 ? 2 * value - 1 : -2 * value);

    private void Bit(int bit)
    {
        if (_bitsInLast == 8) { _bytes.Add(0); _bitsInLast = 0; }
        if (bit != 0) _bytes[^1] |= (byte)(0x80 >> _bitsInLast);
        _bitsInLast++;
    }

    /// <summary>rbsp_trailing_bits(): a one bit, then zeros to the byte boundary.</summary>
    public byte[] Rbsp()
    {
        Bit(1);
        _bitsInLast = 8;
        return [.. _bytes];
    }

    /// <summary>The RBSP as a NAL unit of <paramref name="type"/>: header, then emulation prevention.</summary>
    public byte[] Nal(int type) => ToNal(type, Rbsp());

    public static byte[] ToNal(int type, byte[] rbsp)
    {
        var nal = new List<byte> { (byte)(type << 1), 1 };
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3) { nal.Add(3); zeros = 0; }
            nal.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. nal];
    }

    /// <summary>
    /// A general profile_tier_level for one sub-layer: profile <paramref name="profileIdc"/>, its
    /// compatibility bit, the four source flags (progressive, frame-only) and the given further constraint
    /// bits, then the level.
    /// </summary>
    public HevcBitWriter ProfileTierLevel(int profileIdc, int levelIdc, bool highTier = false, ulong rextConstraints = 0)
    {
        U(2, 0).Flag(highTier).U(5, profileIdc);
        U(32, 1L << (31 - profileIdc));
        U(4, 0b1001);                // progressive, not interlaced, not non-packed... frame-only
        U(9, (long)rextConstraints); // max_12bit .. lower_bit_rate
        U(35, 0);                    // the rest of the 44 bits that follow the four source flags
        return U(8, levelIdc);
    }
}
