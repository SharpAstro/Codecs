namespace SharpAstro.Jxr;

/// <summary>
/// FlexBits refinement layer — T.832 §8.7.19 (MB_HP_FLEX, MB_FLEXBITS,
/// BLOCK_FLEXBITS, DECODE_FLEX). Provides the low-significance bits of
/// each HP AC coefficient that the VLC pass truncated.
/// </summary>
/// <remarks>
/// <para>The VLC pass emits each HP coefficient as <c>vlcValue =
/// trueValue &gt;&gt; iModelBits</c> (a signed integer). FlexBits then refines
/// by appending the lower <c>iFlexBitsLeft = max(0, iModelBits -
/// trimFlexBits)</c> bits. The reconstructed coefficient is
/// <c>(vlcValue &lt;&lt; iModelBits) + (flexRef &lt;&lt; trimFlexBits)</c>
/// — VLC owns the high bits, FlexBits owns the low bits, with TRIM_FLEXBITS
/// optionally dropping LSBs to reduce bitrate.</para>
/// <para>Sign handling: when the VLC coefficient is already non-zero its
/// sign carries through to the refined value. When the VLC coefficient is
/// zero AND the refinement is non-zero, an extra SIGN_FLAG bit is emitted
/// to resolve the sign.</para>
/// </remarks>
internal static class BlockFlexBits
{
    /// <summary>
    /// iTranspose444 from T.832 §8.7.19.2 — the order BLOCK_FLEXBITS visits
    /// HP AC positions. Position 0 (block DC) is skipped (n starts at 1).
    /// </summary>
    public static ReadOnlySpan<byte> Transpose444 =>
        [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

    /// <summary>
    /// Encoder: write the FlexBits refinement for a single AC coefficient.
    /// <paramref name="trueAbsLow"/> is the absolute value of the low
    /// <paramref name="iFlexBitsLeft"/> bits that the VLC pass discarded.
    /// </summary>
    /// <param name="vlcValue">The signed value from the VLC pass (the
    /// high bits). Determines whether FlexBits needs to emit its own sign
    /// (when <c>vlcValue == 0</c>).</param>
    /// <param name="signedRefinement">The refinement value to encode —
    /// already in the same sign convention as the original full coefficient.
    /// Its absolute value is masked to <paramref name="iFlexBitsLeft"/>
    /// bits when emitted; the SIGN_FLAG is added only when the VLC value
    /// was zero and this refinement is non-zero.</param>
    public static void EncodeOne(
        BitWriter writer, int vlcValue, int signedRefinement, int iFlexBitsLeft)
    {
        if (iFlexBitsLeft <= 0)
            return; // no FLEX_REF bits — caller's refinement must be zero in this case.

        var absRef = signedRefinement < 0 ? -signedRefinement : signedRefinement;
        writer.WriteBits((uint)absRef, iFlexBitsLeft);

        // SIGN_FLAG is only emitted when the VLC sign is unknown (VLC=0) and the
        // refinement is non-zero. Otherwise the sign is implicit (inherited or
        // unused).
        if (vlcValue == 0 && absRef != 0)
            writer.WriteBit(signedRefinement < 0);
    }

    /// <summary>
    /// Decoder: read the FlexBits refinement for a single AC coefficient.
    /// Returns the signed refinement value (the lower bits of the true
    /// coefficient, with sign inherited from <paramref name="vlcValue"/>
    /// when non-zero, otherwise from an explicit SIGN_FLAG).
    /// </summary>
    public static int DecodeOne(ref BitReader reader, int vlcValue, int iFlexBitsLeft)
    {
        if (iFlexBitsLeft <= 0) return 0;

        var absRef = (int)reader.ReadBits(iFlexBitsLeft);
        if (vlcValue > 0) return absRef;
        if (vlcValue < 0) return -absRef;
        // vlcValue == 0: SIGN_FLAG only present when absRef != 0.
        if (absRef == 0) return 0;
        return reader.ReadBit() ? -absRef : absRef;
    }

    /// <summary>
    /// Encode the FlexBits for one 4×4 HP block — 15 AC positions visited
    /// in <see cref="Transpose444"/> order, position 0 (block DC) skipped.
    /// </summary>
    /// <param name="vlcBlock">15 VLC values at AC positions 1..15 (caller
    /// provides them indexed 0..14).</param>
    /// <param name="refBlock">15 refinement values in the same order.</param>
    public static void EncodeBlock(BitWriter writer,
        ReadOnlySpan<int> vlcBlock, ReadOnlySpan<int> refBlock, int iFlexBitsLeft)
    {
        if (vlcBlock.Length != 15 || refBlock.Length != 15)
            throw new ArgumentException("BlockFlexBits encode expects 15 AC slots");
        for (var n = 1; n < 16; n++)
        {
            var slot = Transpose444[n] - 1; // n=1..15 ↔ slot in 0..14
            EncodeOne(writer, vlcBlock[slot], refBlock[slot], iFlexBitsLeft);
        }
    }

    /// <summary>Decode the FlexBits for one 4×4 HP block — mirror of <see cref="EncodeBlock"/>.</summary>
    public static void DecodeBlock(ref BitReader reader,
        ReadOnlySpan<int> vlcBlock, Span<int> refBlock, int iFlexBitsLeft)
    {
        if (vlcBlock.Length != 15 || refBlock.Length != 15)
            throw new ArgumentException("BlockFlexBits decode expects 15 AC slots");
        for (var n = 1; n < 16; n++)
        {
            var slot = Transpose444[n] - 1;
            refBlock[slot] = DecodeOne(ref reader, vlcBlock[slot], iFlexBitsLeft);
        }
    }
}
