namespace SharpAstro.Jxr;

/// <summary>
/// Macroblock-level DC-band encoder/decoder — T.832 §8.7.11 (MB_DC)
/// plus the embedded DECODE_DC of §8.7.12. Encodes the super-DC value
/// of each colour component of one macroblock.
/// </summary>
/// <remarks>
/// Two paths depending on <see cref="JxrInternalColorFormat"/>:
/// <list type="bullet">
///   <item>YOnly / YUVK / NComponent — per-component <c>IS_DC_CH_FLAG</c>
///     (1-bit FLC signalling "absolute level &gt; 0 above iModelBits")
///     followed by DECODE_DC for each.</item>
///   <item>RGB / YUV* — joint <c>VAL_DC_YUV</c> VLC (Table 51) packing the
///     three Y/U/V has-non-zero flags in a single code, followed by
///     three DECODE_DC calls.</item>
/// </list>
/// DECODE_DC itself splits the DC magnitude into a high part coded via
/// <see cref="AbsLevel"/> (offset by -1, since DC = 0 is signalled via
/// the absence of bAbsLevel) plus a low FLC refinement of
/// <c>iModelBits</c> bits, then a sign bit when the reconstructed DC is
/// non-zero. The <c>iModelBits</c> width is captured at the start of
/// the MB and stays constant through it; the post-MB
/// <see cref="CoefficientModel.Update"/> may evolve it for the next MB.
/// </remarks>
public static class MbDc
{
    /// <summary>
    /// Encode the per-component DC values of one macroblock.
    /// <paramref name="dcValues"/> must hold at least <paramref name="numComponents"/>
    /// signed ints (the super-DC for each colour plane).
    /// </summary>
    public static void EncodeMb(
        BitWriter writer,
        MbDcState state,
        JxrInternalColorFormat format,
        int numComponents,
        ReadOnlySpan<int> dcValues)
    {
        if (dcValues.Length < numComponents)
            throw new ArgumentException($"dcValues must hold ≥ {numComponents} ints", nameof(dcValues));

        var modelBitsLum = state.Model.MBits0;
        var modelBitsChr = state.Model.MBits1;
        var iLapMeanLum = 0;
        var iLapMeanChr = 0;

        if (IsPerComponentDcFlagFormat(format))
        {
            // Per-component IS_DC_CH_FLAG path.
            for (var c = 0; c < numComponents; c++)
            {
                var bChroma = c != 0;
                var mb = bChroma ? modelBitsChr : modelBitsLum;
                var dc = dcValues[c];
                var absDc = dc < 0 ? -dc : dc;
                var high = mb > 0 ? absDc >> mb : absDc;
                var bAbsLevel = high > 0;

                writer.WriteBit(bAbsLevel); // IS_DC_CH_FLAG
                if (bAbsLevel)
                {
                    if (bChroma) iLapMeanChr++;
                    else iLapMeanLum++;
                }

                if (bChroma) EncodeDc(writer, ref state.AbsLevelChr, mb, bAbsLevel, dc);
                else         EncodeDc(writer, ref state.AbsLevelLum, mb, bAbsLevel, dc);
            }
        }
        else
        {
            // VAL_DC_YUV joint path for RGB / YUV*.
            if (numComponents < 3)
                throw new ArgumentException("VAL_DC_YUV path requires 3+ components (Y, U, V)", nameof(numComponents));

            var dcY = dcValues[0];
            var dcU = dcValues[1];
            var dcV = dcValues[2];
            var bAbsY = HasHighBits(dcY, modelBitsLum);
            var bAbsU = HasHighBits(dcU, modelBitsChr);
            var bAbsV = HasHighBits(dcV, modelBitsChr);
            var valDcYuv = (bAbsY ? 4 : 0) | (bAbsU ? 2 : 0) | (bAbsV ? 1 : 0);
            VlcTables.ValDcYuv.Encode(writer, valDcYuv);
            if (bAbsY) iLapMeanLum++;
            EncodeDc(writer, ref state.AbsLevelLum, modelBitsLum, bAbsY, dcY);
            if (bAbsU) iLapMeanChr++;
            EncodeDc(writer, ref state.AbsLevelChr, modelBitsChr, bAbsU, dcU);
            if (bAbsV) iLapMeanChr++;
            EncodeDc(writer, ref state.AbsLevelChr, modelBitsChr, bAbsV, dcV);
        }

        CoefficientModel.Update(
            ref state.Model,
            iLapMeanLum,
            iLapMeanChr,
            CoefficientModel.Band.Dc,
            format,
            numComponents);
    }

    /// <summary>Decode the per-component DC values of one macroblock — dual of <see cref="EncodeMb"/>.</summary>
    public static void DecodeMb(
        ref BitReader reader,
        MbDcState state,
        JxrInternalColorFormat format,
        int numComponents,
        Span<int> dcValuesOut)
    {
        if (dcValuesOut.Length < numComponents)
            throw new ArgumentException($"dcValuesOut must hold ≥ {numComponents} ints", nameof(dcValuesOut));

        var modelBitsLum = state.Model.MBits0;
        var modelBitsChr = state.Model.MBits1;
        var iLapMeanLum = 0;
        var iLapMeanChr = 0;

        if (IsPerComponentDcFlagFormat(format))
        {
            for (var c = 0; c < numComponents; c++)
            {
                var bChroma = c != 0;
                var mb = bChroma ? modelBitsChr : modelBitsLum;
                var bAbsLevel = reader.ReadBit();
                if (bAbsLevel)
                {
                    if (bChroma) iLapMeanChr++;
                    else iLapMeanLum++;
                }

                dcValuesOut[c] = bChroma
                    ? DecodeDc(ref reader, ref state.AbsLevelChr, mb, bAbsLevel)
                    : DecodeDc(ref reader, ref state.AbsLevelLum, mb, bAbsLevel);
            }
        }
        else
        {
            if (numComponents < 3)
                throw new ArgumentException("VAL_DC_YUV path requires 3+ components", nameof(numComponents));

            var valDcYuv = VlcTables.ValDcYuv.Decode(ref reader);
            var bAbsY = (valDcYuv & 4) != 0;
            var bAbsU = (valDcYuv & 2) != 0;
            var bAbsV = (valDcYuv & 1) != 0;

            if (bAbsY) iLapMeanLum++;
            dcValuesOut[0] = DecodeDc(ref reader, ref state.AbsLevelLum, modelBitsLum, bAbsY);
            if (bAbsU) iLapMeanChr++;
            dcValuesOut[1] = DecodeDc(ref reader, ref state.AbsLevelChr, modelBitsChr, bAbsU);
            if (bAbsV) iLapMeanChr++;
            dcValuesOut[2] = DecodeDc(ref reader, ref state.AbsLevelChr, modelBitsChr, bAbsV);
        }

        CoefficientModel.Update(
            ref state.Model,
            iLapMeanLum,
            iLapMeanChr,
            CoefficientModel.Band.Dc,
            format,
            numComponents);
    }

    // -----------------------------------------------------------------------
    // DECODE_DC / ENCODE_DC inner pair — T.832 §8.7.12 / Table 49
    // -----------------------------------------------------------------------

    private static int DecodeDc(ref BitReader reader, ref AdaptiveVlcState absLevelState, int iModelBits, bool bAbsLevel)
    {
        var iDc = 0;
        if (bAbsLevel)
            iDc = AbsLevel.Decode(ref reader, ref absLevelState) - 1;
        if (iModelBits > 0)
        {
            var iDcRef = (int)reader.ReadBits(iModelBits);
            iDc = (iDc << iModelBits) | iDcRef;
        }
        if (iDc != 0 && reader.ReadBit())
            iDc = -iDc;
        return iDc;
    }

    private static void EncodeDc(BitWriter writer, ref AdaptiveVlcState absLevelState, int iModelBits, bool bAbsLevel, int iDc)
    {
        var absDc = iDc < 0 ? -iDc : iDc;
        var high = iModelBits > 0 ? absDc >> iModelBits : absDc;
        var low = iModelBits > 0 ? absDc & ((1 << iModelBits) - 1) : 0;
        if (bAbsLevel != (high > 0))
            throw new ArgumentException($"bAbsLevel ({bAbsLevel}) inconsistent with high bits ({high}) for iDc={iDc} iModelBits={iModelBits}");
        if (bAbsLevel)
            AbsLevel.Encode(writer, ref absLevelState, high + 1);
        if (iModelBits > 0)
            writer.WriteBits((uint)low, iModelBits);
        if (iDc != 0)
            writer.WriteBit(iDc < 0);
    }

    private static bool IsPerComponentDcFlagFormat(JxrInternalColorFormat f) =>
        f == JxrInternalColorFormat.YOnly ||
        f == JxrInternalColorFormat.YUVK ||
        f == JxrInternalColorFormat.NComponent;

    private static bool HasHighBits(int value, int modelBits)
    {
        var abs = value < 0 ? -value : value;
        return modelBits > 0 ? (abs >> modelBits) > 0 : abs > 0;
    }
}
