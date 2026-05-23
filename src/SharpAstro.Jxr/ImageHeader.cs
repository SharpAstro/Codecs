namespace SharpAstro.Jxr;

/// <summary>
/// IMAGE_HEADER from the JXR codestream — T.832 §8.3. The 64-bit
/// <c>GDI_SIGNATURE</c> ("WMPHOTO\0") marks the start of every JXR
/// codestream; the rest of the header packs format, sizing, tiling,
/// overlap mode, and orientation hints into a compact bitstream prefix.
/// </summary>
/// <remarks>
/// This first cut intentionally restricts tiling and windowing:
/// <list type="bullet">
///   <item><c>TILING_FLAG = false</c> (single-tile images only)</item>
///   <item><c>WINDOWING_FLAG = false</c> (no margins)</item>
///   <item><c>INDEX_TABLE_PRESENT_FLAG = false</c> (spatial mode only)</item>
/// </list>
/// Those features extend the syntax with additional fields and add MB-grid
/// bookkeeping that lands in a follow-on commit.
/// </remarks>
public sealed class ImageHeader
{
    /// <summary>Fixed signature "WMPHOTO\0" — required as bytes 0..7 of the codestream.</summary>
    public const ulong GdiSignature = 0x574D50484F544F00UL;

    // Flag bits packed across bytes 8 and 9 of the codestream.
    public bool HardTilingFlag;
    public bool FrequencyModeCodestreamFlag;
    public int SpatialXfrmSubordinate;      // u(3), 0..7
    public bool IndexTablePresentFlag;
    public int OverlapMode;                 // u(2), 0..2
    public bool ShortHeaderFlag;
    public bool LongWordFlag;
    public bool WindowingFlag;
    public bool TrimFlexBitsFlag;
    public bool RedBlueNotSwappedFlag;
    public bool PremultipliedAlphaFlag;
    public bool AlphaImagePlaneFlag;

    public JxrOutputColorFormat OutputClrFmt;
    public JxrOutputBitDepth OutputBitDepth;

    public uint WidthMinus1;
    public uint HeightMinus1;

    // Tile fields — only valid when TILING_FLAG is true. Not yet exposed.
    // public int NumVerTilesMinus1;
    // public int NumHorTilesMinus1;
    // public ushort[] TileWidthInMb;
    // public ushort[] TileHeightInMb;

    // Windowing fields — only valid when WINDOWING_FLAG is true.
    // public int TopMargin, LeftMargin, BottomMargin, RightMargin;

    /// <summary>
    /// Write this header to <paramref name="writer"/> in T.832 §8.3 syntax.
    /// The bit fields pack tightly; callers are responsible for byte
    /// alignment of subsequent structures (which the header itself ends on
    /// a byte boundary thanks to the way RESERVED_B / RESERVED_C / OUTPUT_*
    /// fields combine).
    /// </summary>
    public void Write(BitWriter writer)
    {
        if (WindowingFlag)
            throw new NotSupportedException("WINDOWING_FLAG=true not yet supported in ImageHeader.Write");
        if (IndexTablePresentFlag)
            throw new NotSupportedException("INDEX_TABLE_PRESENT_FLAG=true not yet supported");

        // GDI_SIGNATURE 64 bits big-endian (WMPHOTO\0 = 0x574D50484F544F00)
        writer.WriteBits((uint)(GdiSignature >> 32), 32);
        writer.WriteBits((uint)(GdiSignature & 0xFFFFFFFFu), 32);

        writer.WriteBits(1, 4); // RESERVED_B == 1 (T.832 8.3.3)
        writer.WriteBit(HardTilingFlag);
        writer.WriteBits(1, 3); // RESERVED_C == 1 (T.832 8.3.5)
        writer.WriteBit(false); // TILING_FLAG — single tile only for now
        writer.WriteBit(FrequencyModeCodestreamFlag);
        writer.WriteBits((uint)SpatialXfrmSubordinate, 3);
        writer.WriteBit(IndexTablePresentFlag);
        writer.WriteBits((uint)OverlapMode, 2);
        writer.WriteBit(ShortHeaderFlag);
        writer.WriteBit(LongWordFlag);
        writer.WriteBit(WindowingFlag);
        writer.WriteBit(TrimFlexBitsFlag);
        writer.WriteBit(false); // RESERVED_D == 0 (T.832 8.3.15 specifies value)
        writer.WriteBit(RedBlueNotSwappedFlag);
        writer.WriteBit(PremultipliedAlphaFlag);
        writer.WriteBit(AlphaImagePlaneFlag);
        writer.WriteBits((uint)OutputClrFmt, 4);
        writer.WriteBits((uint)OutputBitDepth, 4);

        if (ShortHeaderFlag)
        {
            writer.WriteBits(WidthMinus1, 16);
            writer.WriteBits(HeightMinus1, 16);
        }
        else
        {
            writer.WriteBits(WidthMinus1, 32);
            writer.WriteBits(HeightMinus1, 32);
        }
    }

    /// <summary>Read an IMAGE_HEADER from <paramref name="reader"/>.</summary>
    public static ImageHeader Read(ref BitReader reader)
    {
        // GDI_SIGNATURE 64 bits
        var sigHi = reader.ReadBits(32);
        var sigLo = reader.ReadBits(32);
        var sig = ((ulong)sigHi << 32) | sigLo;
        if (sig != GdiSignature)
            throw new InvalidDataException($"GDI_SIGNATURE mismatch: expected 0x{GdiSignature:X16}, got 0x{sig:X16}");

        var reservedB = reader.ReadBits(4);
        if (reservedB != 1)
            throw new InvalidDataException($"RESERVED_B must be 1 (T.832 8.3.3), got {reservedB}");

        var header = new ImageHeader
        {
            HardTilingFlag = reader.ReadBit(),
        };
        reader.ReadBits(3); // RESERVED_C — decoder must ignore
        var tilingFlag = reader.ReadBit();
        if (tilingFlag)
            throw new NotSupportedException("Tiled codestreams not yet supported (TILING_FLAG=true)");
        header.FrequencyModeCodestreamFlag = reader.ReadBit();
        header.SpatialXfrmSubordinate = (int)reader.ReadBits(3);
        header.IndexTablePresentFlag = reader.ReadBit();
        header.OverlapMode = (int)reader.ReadBits(2);
        header.ShortHeaderFlag = reader.ReadBit();
        header.LongWordFlag = reader.ReadBit();
        header.WindowingFlag = reader.ReadBit();
        if (header.WindowingFlag)
            throw new NotSupportedException("Windowed codestreams not yet supported (WINDOWING_FLAG=true)");
        header.TrimFlexBitsFlag = reader.ReadBit();
        reader.ReadBit(); // RESERVED_D
        header.RedBlueNotSwappedFlag = reader.ReadBit();
        header.PremultipliedAlphaFlag = reader.ReadBit();
        header.AlphaImagePlaneFlag = reader.ReadBit();
        header.OutputClrFmt = (JxrOutputColorFormat)reader.ReadBits(4);
        header.OutputBitDepth = (JxrOutputBitDepth)reader.ReadBits(4);

        if (header.ShortHeaderFlag)
        {
            header.WidthMinus1 = reader.ReadBits(16);
            header.HeightMinus1 = reader.ReadBits(16);
        }
        else
        {
            header.WidthMinus1 = reader.ReadBits(32);
            header.HeightMinus1 = reader.ReadBits(32);
        }
        return header;
    }
}
