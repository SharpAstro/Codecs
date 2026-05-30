using System.Numerics;

namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL hybrid integer configuration (ISO/IEC 18181-1 §C.2.3, "Hybrid integer encoding").
/// A token decoded from the entropy stream is expanded into a full integer: tokens below
/// <see cref="Split"/> are literal; larger tokens carry <c>msb_in_token</c>/<c>lsb_in_token</c>
/// bits inline, and the remaining magnitude follows as raw trailing bits. Faithful port of
/// jxl-coding (lib.rs <c>IntegerConfig</c> / <c>read_uint_prefilled</c>).
/// </summary>
internal readonly struct JxlIntegerConfig
{
    public int SplitExponent { get; init; }
    public uint Split { get; init; }
    public int MsbInToken { get; init; }
    public int LsbInToken { get; init; }

    public static JxlIntegerConfig Create(int splitExponent, int msbInToken, int lsbInToken) => new()
    {
        SplitExponent = splitExponent,
        Split = 1u << splitExponent,
        MsbInToken = msbInToken,
        LsbInToken = lsbInToken,
    };

    public static JxlIntegerConfig Parse(ref JxlBitReader br, int logAlphabetSize)
    {
        int splitExponent = (int)br.ReadBits(AddLog2Ceil((uint)logAlphabetSize));
        int msb = 0, lsb = 0;
        if (splitExponent != logAlphabetSize)
        {
            msb = (int)br.ReadBits(AddLog2Ceil((uint)splitExponent));
            if (msb > splitExponent)
                throw new InvalidDataException("JPEG XL invalid hybrid-uint config (msb_in_token > split_exponent).");
            lsb = (int)br.ReadBits(AddLog2Ceil((uint)(splitExponent - msb)));
        }
        if (lsb + msb > splitExponent)
            throw new InvalidDataException("JPEG XL invalid hybrid-uint config (msb_in_token + lsb_in_token > split_exponent).");
        return Create(splitExponent, msb, lsb);
    }

    public void Write(JxlBitWriter bw, int logAlphabetSize)
    {
        bw.WriteBits((uint)SplitExponent, AddLog2Ceil((uint)logAlphabetSize));
        if (SplitExponent != logAlphabetSize)
        {
            bw.WriteBits((uint)MsbInToken, AddLog2Ceil((uint)SplitExponent));
            bw.WriteBits((uint)LsbInToken, AddLog2Ceil((uint)(SplitExponent - MsbInToken)));
        }
    }

    /// <summary>Expands a decoded token into the full integer, reading any trailing raw bits.</summary>
    public uint ReadUint(ref JxlBitReader br, uint token)
    {
        if (token < Split)
            return token;

        int ml = MsbInToken + LsbInToken;
        int n = (SplitExponent - ml + (int)((token - Split) >> ml)) & 31;
        ulong restBits = br.PeekBits(n);
        br.ConsumeBits(n);

        uint lowBits = token & ((1u << LsbInToken) - 1);
        uint t = (token >> LsbInToken) & ((1u << MsbInToken) - 1);
        t |= 1u << MsbInToken;
        ulong result = ((((ulong)t << n) | restBits) << LsbInToken) | lowBits;
        return (uint)result;
    }

    /// <summary>
    /// Inverse of <see cref="ReadUint"/>: split <paramref name="value"/> into its entropy token
    /// plus the trailing raw bits to emit immediately after the token.
    /// </summary>
    public uint EncodeToken(uint value, out uint restBits, out int restBitCount)
    {
        if (value < Split)
        {
            restBits = 0;
            restBitCount = 0;
            return value;
        }

        int n = 31 - BitOperations.LeadingZeroCount(value); // floor(log2(value)), the leading bit position
        uint low = value & ((1u << LsbInToken) - 1);
        uint high = (value >> (n - MsbInToken)) & ((1u << MsbInToken) - 1);
        uint token = Split + ((uint)(n - SplitExponent) << (MsbInToken + LsbInToken)) + (high << LsbInToken) + low;

        restBitCount = n - MsbInToken - LsbInToken;
        restBits = (value >> LsbInToken) & ((1u << restBitCount) - 1);
        return token;
    }

    /// <summary>
    /// <c>add_log2_ceil(x) = ceil(log2(x + 1))</c> — the number of bits needed to encode a value
    /// in <c>[0, x]</c> (jxl-coding lib.rs). Used to size the config's own field widths.
    /// </summary>
    internal static int AddLog2Ceil(uint x)
    {
        if (x >= 0x80000000)
            return 32;
        uint v = x + 1;          // next_power_of_two(v).trailing_zeros()
        if (v <= 1)
            return 0;
        return 32 - BitOperations.LeadingZeroCount(v - 1);
    }
}
