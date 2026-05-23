namespace SharpAstro.Jxr;

/// <summary>
/// IMAGE_PLANE_HEADER from the JXR codestream — T.832 §8.4. Each image
/// plane (luma plane and optional alpha plane) is prefixed by one of these
/// structures, which selects internal colour format, declares which subbands
/// (DC / LP / HP / FlexBits) are present, and carries the uniform-quantization
/// step sizes for each present band.
/// </summary>
/// <remarks>
/// This first cut targets the common single-tile spatial-mode encoder path
/// and intentionally restricts:
/// <list type="bullet">
///   <item>Non-uniform per-MB quantization (the IMAGE_PLANE_UNIFORM_FLAG=false
///         path lands with the tile-header work). Only uniform-quant is supported.</item>
///   <item>YUV chroma centering — INTERNAL_CLR_FMT must currently be one of
///         YOnly / YUV444 / YUVK / NComponent / Rgb / Rgbe. YUV420/422 add
///         CHROMA_CENTERING_{X,Y} fields and are deferred.</item>
///   <item>NComponent extension (NUM_COMPONENTS_MINUS1 == 0xF). NumComponents
///         is therefore restricted to 1..16.</item>
/// </list>
/// </remarks>
public sealed class ImagePlaneHeader
{
    public JxrInternalColorFormat InternalClrFmt;
    public bool ScaledFlag;                 // T.832 8.4.3 — pre-scaling stage active
    public JxrBandsPresent BandsPresent;

    /// <summary>NUM_COMPONENTS_MINUS1 + 1 — only meaningful when INTERNAL_CLR_FMT == NComponent.</summary>
    public int NumComponents = 1;

    /// <summary>SHIFT_BITS — present only for OUTPUT_BITDEPTH in {Bd16, Bd16S, Bd32S}.</summary>
    public byte ShiftBits;

    /// <summary>LEN_MANTISSA — present only for OUTPUT_BITDEPTH == Bd32F.</summary>
    public byte LenMantissa;

    /// <summary>EXP_BIAS — present only for OUTPUT_BITDEPTH == Bd32F (signed 8-bit).</summary>
    public sbyte ExpBias;

    /// <summary>DC band uniform-quantization step. Always present.</summary>
    public byte DcQuant;

    /// <summary>LP band uniform-quantization step (only meaningful when LP band is present and not aliased to DC).</summary>
    public byte LpQuant;

    /// <summary>True when LP band reuses DC quantization (saves bits in the codestream).</summary>
    public bool UseDcQpForLp;

    /// <summary>HP band uniform-quantization step (only meaningful when HP band is present and not aliased to LP).</summary>
    public byte HpQuant;

    /// <summary>True when HP band reuses LP quantization.</summary>
    public bool UseLpQpForHp;

    /// <summary>
    /// Write this header to <paramref name="writer"/>. Caller supplies the
    /// <paramref name="outputBitDepth"/> from the enclosing IMAGE_HEADER —
    /// the bitstream layout of SHIFT_BITS / LEN_MANTISSA / EXP_BIAS depends on it.
    /// </summary>
    public void Write(BitWriter writer, JxrOutputBitDepth outputBitDepth)
    {
        if (InternalClrFmt == JxrInternalColorFormat.YUV420 || InternalClrFmt == JxrInternalColorFormat.YUV422)
            throw new NotSupportedException("YUV420/YUV422 internal formats not yet supported in IMAGE_PLANE_HEADER.Write");

        writer.WriteBits((uint)InternalClrFmt, 3);
        writer.WriteBit(ScaledFlag);
        writer.WriteBits((uint)BandsPresent, 4);

        // CHROMA_CENTERING_{X,Y} blocks for YUV420/422 elided (rejected above).
        if (InternalClrFmt == JxrInternalColorFormat.YUV444)
        {
            // RESERVED_F u(4) per T.832 8.4 syntax for YUV444 padding.
            writer.WriteBits(0, 4);
        }

        if (InternalClrFmt == JxrInternalColorFormat.NComponent)
        {
            if (NumComponents is < 1 or > 16)
                throw new NotSupportedException($"NComponent NumComponents must be 1..16 (extension path not yet supported), got {NumComponents}");
            writer.WriteBits((uint)(NumComponents - 1), 4);
        }

        switch (outputBitDepth)
        {
            case JxrOutputBitDepth.Bd16:
            case JxrOutputBitDepth.Bd16S:
            case JxrOutputBitDepth.Bd32S:
                writer.WriteBits(ShiftBits, 8);
                break;
            case JxrOutputBitDepth.Bd16F:
            case JxrOutputBitDepth.Bd32F:
                writer.WriteBits(LenMantissa, 8);
                writer.WriteBits((uint)(byte)ExpBias, 8); // EXP_BIAS = i(8); raw 8 bits, two's-complement.
                break;
        }

        // DC band — uniform-quant only for this cut.
        writer.WriteBit(true);             // DC_IMAGE_PLANE_UNIFORM_FLAG
        writer.WriteBits(DcQuant, 8);      // DC_QUANT

        if (BandsPresent != JxrBandsPresent.DcOnly)
        {
            writer.WriteBit(false);        // RESERVED_I_BIT
            writer.WriteBit(UseDcQpForLp); // USE_DC_QP_FLAG
            if (!UseDcQpForLp)
            {
                writer.WriteBit(true);     // LP_IMAGE_PLANE_UNIFORM_FLAG
                writer.WriteBits(LpQuant, 8);
            }
        }

        if (BandsPresent != JxrBandsPresent.DcOnly && BandsPresent != JxrBandsPresent.NoHighpass)
        {
            writer.WriteBit(false);        // RESERVED_J_BIT
            writer.WriteBit(UseLpQpForHp); // USE_LP_QP_FLAG
            if (!UseLpQpForHp)
            {
                writer.WriteBit(true);     // HP_IMAGE_PLANE_UNIFORM_FLAG
                writer.WriteBits(HpQuant, 8);
            }
        }

        // byte_alignment() — pad to byte boundary (T.832 5.3.3).
        WriteByteAlignment(writer);
    }

    /// <summary>Read an IMAGE_PLANE_HEADER from <paramref name="reader"/>.</summary>
    public static ImagePlaneHeader Read(ref BitReader reader, JxrOutputBitDepth outputBitDepth)
    {
        var h = new ImagePlaneHeader
        {
            InternalClrFmt = (JxrInternalColorFormat)reader.ReadBits(3),
            ScaledFlag = reader.ReadBit(),
            BandsPresent = (JxrBandsPresent)reader.ReadBits(4),
        };

        if (h.InternalClrFmt == JxrInternalColorFormat.YUV420 || h.InternalClrFmt == JxrInternalColorFormat.YUV422)
            throw new NotSupportedException($"YUV420/YUV422 internal formats not yet supported (CHROMA_CENTERING_{{X,Y}} parsing pending), got {h.InternalClrFmt}");
        if (h.InternalClrFmt == JxrInternalColorFormat.YUV444)
        {
            reader.SkipBits(4); // RESERVED_F
        }

        // NumComponents is bitstream-encoded only for NComponent; for the named
        // formats it's implied by InternalClrFmt — derive it so downstream code
        // (e.g. MbDc.DecodeMb's VAL_DC_YUV path) sees the correct component count.
        h.NumComponents = ComponentCountFor(h.InternalClrFmt);

        if (h.InternalClrFmt == JxrInternalColorFormat.NComponent)
        {
            var numMinus1 = (int)reader.ReadBits(4);
            if (numMinus1 == 0xF)
                throw new NotSupportedException("NComponent extension (>=17 components) not yet supported");
            h.NumComponents = numMinus1 + 1;
        }

        switch (outputBitDepth)
        {
            case JxrOutputBitDepth.Bd16:
            case JxrOutputBitDepth.Bd16S:
            case JxrOutputBitDepth.Bd32S:
                h.ShiftBits = (byte)reader.ReadBits(8);
                break;
            case JxrOutputBitDepth.Bd16F:
            case JxrOutputBitDepth.Bd32F:
                h.LenMantissa = (byte)reader.ReadBits(8);
                h.ExpBias = unchecked((sbyte)reader.ReadBits(8));
                break;
        }

        // DC band — only the uniform path is supported in this cut.
        var dcUniform = reader.ReadBit();
        if (!dcUniform)
            throw new NotSupportedException("Non-uniform per-MB DC quantization not yet supported");
        h.DcQuant = (byte)reader.ReadBits(8);

        if (h.BandsPresent != JxrBandsPresent.DcOnly)
        {
            reader.ReadBit(); // RESERVED_I_BIT — decoder ignores
            h.UseDcQpForLp = reader.ReadBit();
            if (!h.UseDcQpForLp)
            {
                var lpUniform = reader.ReadBit();
                if (!lpUniform)
                    throw new NotSupportedException("Non-uniform per-MB LP quantization not yet supported");
                h.LpQuant = (byte)reader.ReadBits(8);
            }
        }

        if (h.BandsPresent != JxrBandsPresent.DcOnly && h.BandsPresent != JxrBandsPresent.NoHighpass)
        {
            reader.ReadBit(); // RESERVED_J_BIT
            h.UseLpQpForHp = reader.ReadBit();
            if (!h.UseLpQpForHp)
            {
                var hpUniform = reader.ReadBit();
                if (!hpUniform)
                    throw new NotSupportedException("Non-uniform per-MB HP quantization not yet supported");
                h.HpQuant = (byte)reader.ReadBits(8);
            }
        }

        AlignToByte(ref reader);
        return h;
    }

    private static void WriteByteAlignment(BitWriter writer)
    {
        // Pad with zero bits until on a byte boundary. WriteBit increments the
        // byte cursor when the eighth bit lands, so just pad until BitPosition
        // is a multiple of 8.
        while ((writer.BitPosition & 7) != 0) writer.WriteBit(false);
    }

    private static void AlignToByte(ref BitReader reader)
    {
        var slack = (8 - (reader.BitPosition & 7)) & 7;
        if (slack > 0) reader.SkipBits(slack);
    }

    /// <summary>
    /// Component count implied by <paramref name="fmt"/>. For NComponent the
    /// real count lives in the bitstream and overrides this default.
    /// </summary>
    public static int ComponentCountFor(JxrInternalColorFormat fmt) => fmt switch
    {
        JxrInternalColorFormat.YOnly      => 1,
        JxrInternalColorFormat.YUV420     => 3,
        JxrInternalColorFormat.YUV422     => 3,
        JxrInternalColorFormat.YUV444     => 3,
        JxrInternalColorFormat.YUVK       => 4,
        JxrInternalColorFormat.Rgb        => 3,
        JxrInternalColorFormat.Rgbe       => 4,
        JxrInternalColorFormat.NComponent => 1, // placeholder; real value comes from bitstream
        _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, "unknown internal colour format"),
    };
}
