namespace SharpAstro.Jxr;

/// <summary>
/// Bundles the five AdaptiveVlcState instances that a single
/// <see cref="Block"/> encode/decode call needs. The caller picks the
/// right bundle for the band (DC / LP / HP) and chroma channel of the
/// macroblock currently being processed, and the inner block coder
/// dispatches to <see cref="AbsLevel0"/>/<see cref="AbsLevel1"/> and
/// <see cref="Index0"/>/<see cref="Index1"/> based on the iContext flag
/// that flows through the block's emission.
/// </summary>
public struct BlockCodingContext
{
    public AdaptiveVlcState FirstIndex;
    public AdaptiveVlcState Index0;
    public AdaptiveVlcState Index1;
    public AdaptiveVlcState AbsLevel0;
    public AdaptiveVlcState AbsLevel1;
}

/// <summary>
/// Block-level coefficient encoder/decoder — T.832 §8.7.18.5
/// (DECODE_BLOCK) and its forward dual.
/// </summary>
/// <remarks>
/// Decoded output and encode input share a flat <c>(run, level)*</c>
/// layout: <c>coeff[2k]</c> is the run of zeros before the k-th
/// non-zero coefficient, <c>coeff[2k + 1]</c> is the signed coefficient
/// value. Total length = <c>2 * iNumNonZero</c>.
///
/// <para><c>iLocation</c> is the starting scan step (1 for HP per
/// DECODE_BLOCK_ADAPTIVE in T.832 8.7.18.4). The block has 15 AC
/// positions, so cumulative <c>(run + 1)</c> across all coefficients
/// must keep iLocation ≤ 15.</para>
///
/// The 5-state <see cref="BlockCodingContext"/> threads four discriminants
/// through the block: one for FIRST_INDEX, two for INDEX (context 0 vs 1,
/// dispatched by the <c>iContext</c> bit derived from FIRST_INDEX +
/// subsequent INDEX values), and two for ABS_LEVEL_INDEX (same iContext
/// dispatch).
/// </remarks>
public static class Block
{
    /// <summary>
    /// Decode one block of non-zero coefficients from <paramref name="reader"/>.
    /// <paramref name="coeffOut"/> must be at least 32 ints long; the
    /// first <c>2 * returnValue</c> ints are populated with
    /// <c>(run, level)</c> pairs.
    /// </summary>
    public static int Decode(
        ref BitReader reader,
        ref BlockCodingContext ctx,
        Span<int> coeffOut,
        int iLocation)
    {
        if (coeffOut.Length < 32)
            throw new ArgumentException("coeffOut buffer must be at least 32 ints", nameof(coeffOut));

        var iNumNZ = 1;
        var iFirstIndex = FirstIndexCoding.Decode(ref reader, ref ctx.FirstIndex);
        var signFlag = reader.ReadBit();
        var iSR = iFirstIndex & 1;
        var iSRn = iFirstIndex >> 2;
        var iContext = iSR & iSRn;

        // First coefficient level (magnitude bit at FIRST_INDEX bit 1).
        if ((iFirstIndex & 2) != 0)
        {
            coeffOut[1] = iContext == 1
                ? AbsLevel.Decode(ref reader, ref ctx.AbsLevel1)
                : AbsLevel.Decode(ref reader, ref ctx.AbsLevel0);
        }
        else
        {
            coeffOut[1] = 1;
        }
        if (signFlag) coeffOut[1] = -coeffOut[1];

        // Leading run.
        coeffOut[0] = iSR == 0 ? RunCoding.Decode(ref reader, 15 - iLocation) : 0;
        iLocation += coeffOut[0] + 1;

        // Loop while iSRn signals "more coefficients".
        while (iSRn != 0)
        {
            iSR = iSRn & 1;
            coeffOut[iNumNZ * 2] = iSR == 0 ? RunCoding.Decode(ref reader, 15 - iLocation) : 0;
            iLocation += coeffOut[iNumNZ * 2] + 1;

            var iIndex = iContext == 1
                ? IndexCoding.Decode(ref reader, iLocation, ref ctx.Index1)
                : IndexCoding.Decode(ref reader, iLocation, ref ctx.Index0);

            iSRn = iIndex >> 1;
            iContext &= iSRn;

            var sign = reader.ReadBit();
            int lvl;
            if ((iIndex & 1) != 0)
            {
                lvl = iContext == 1
                    ? AbsLevel.Decode(ref reader, ref ctx.AbsLevel1)
                    : AbsLevel.Decode(ref reader, ref ctx.AbsLevel0);
            }
            else
            {
                lvl = 1;
            }
            coeffOut[iNumNZ * 2 + 1] = sign ? -lvl : lvl;
            iNumNZ++;
        }

        return iNumNZ;
    }

    /// <summary>
    /// Encode one block of non-zero coefficients. <paramref name="coeff"/>
    /// holds <c>2 * iNumNonZero</c> ints in <c>(run, level)*</c> layout.
    /// </summary>
    public static void Encode(
        BitWriter writer,
        ref BlockCodingContext ctx,
        ReadOnlySpan<int> coeff,
        int iNumNonZero,
        int iLocation)
    {
        if (iNumNonZero < 1)
            throw new ArgumentOutOfRangeException(nameof(iNumNonZero), "Block must have at least one non-zero coefficient");
        if (coeff.Length < iNumNonZero * 2)
            throw new ArgumentException("coeff buffer too small for iNumNonZero pairs", nameof(coeff));

        // ---- First coefficient ----------------------------------------
        var firstRun = coeff[0];
        var firstLevel = coeff[1];
        if (firstLevel == 0)
            throw new ArgumentException("First coefficient level must be non-zero");
        var firstAbsLevel = firstLevel < 0 ? -firstLevel : firstLevel;
        var firstSign = firstLevel < 0;
        var firstIsr = firstRun == 0 ? 1 : 0;
        var firstMag = firstAbsLevel > 1 ? 1 : 0;
        int firstIsrn;
        if (iNumNonZero == 1)
        {
            firstIsrn = 0; // last coefficient
        }
        else
        {
            var nextRun = coeff[2];
            firstIsrn = nextRun == 0 ? 1 : 2;
        }
        var firstIndex = firstIsr | (firstMag << 1) | (firstIsrn << 2);
        FirstIndexCoding.Encode(writer, ref ctx.FirstIndex, firstIndex);
        writer.WriteBit(firstSign);

        var iContext = firstIsr & firstIsrn;
        if (firstMag == 1)
        {
            if (iContext == 1) AbsLevel.Encode(writer, ref ctx.AbsLevel1, firstAbsLevel);
            else AbsLevel.Encode(writer, ref ctx.AbsLevel0, firstAbsLevel);
        }

        if (firstIsr == 0)
        {
            // Leading run is non-zero — emit it (iMaxRun = remaining slots).
            RunCoding.Encode(writer, 15 - iLocation, firstRun);
        }
        iLocation += firstRun + 1;

        var iSRn = firstIsrn;

        // ---- Subsequent coefficients ----------------------------------
        for (var k = 1; k < iNumNonZero; k++)
        {
            var run = coeff[k * 2];
            var level = coeff[k * 2 + 1];
            if (level == 0)
                throw new ArgumentException($"Coefficient {k} level must be non-zero");
            var absLevel = level < 0 ? -level : level;
            var sign = level < 0;

            var iSR = iSRn & 1;
            if (iSR == 0)
                RunCoding.Encode(writer, 15 - iLocation, run);
            else if (run != 0)
                throw new ArgumentException($"Coefficient {k} run must be 0 (signalled via prior iSRn=1) but was {run}");
            iLocation += run + 1;

            // Compute INDEX value: bit 0 = magnitude flag, bits 1-2 = iSRn for next.
            var isLast = k == iNumNonZero - 1 ? 1 : 0;
            int isrnNext;
            if (isLast == 1)
            {
                isrnNext = 0;
            }
            else
            {
                var nextRun = coeff[(k + 1) * 2];
                isrnNext = nextRun == 0 ? 1 : 2;
            }
            var mag = absLevel > 1 ? 1 : 0;
            var index = mag | (isrnNext << 1);

            if (iContext == 1) IndexCoding.Encode(writer, iLocation, ref ctx.Index1, index);
            else               IndexCoding.Encode(writer, iLocation, ref ctx.Index0, index);

            iSRn = isrnNext;
            iContext &= iSRn;

            writer.WriteBit(sign);
            if (mag == 1)
            {
                if (iContext == 1) AbsLevel.Encode(writer, ref ctx.AbsLevel1, absLevel);
                else               AbsLevel.Encode(writer, ref ctx.AbsLevel0, absLevel);
            }
        }
    }
}
