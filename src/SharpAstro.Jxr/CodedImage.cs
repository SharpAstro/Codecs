namespace SharpAstro.Jxr;

/// <summary>
/// Top-level <c>CODED_IMAGE</c> structure — T.832 §8.2. Wraps the codestream
/// prologue (<see cref="ImageHeader"/>, primary <see cref="ImagePlaneHeader"/>,
/// <see cref="ProfileLevelInfo"/>) around the coded tile data. For
/// single-tile spatial-mode codestreams the body is a single
/// <see cref="TileSpatial"/> block.
/// </summary>
/// <remarks>
/// <para>Current restrictions match the underlying components:</para>
/// <list type="bullet">
///   <item>Single tile only (<c>TILING_FLAG=false</c>) — no
///         <c>INDEX_TABLE_TILES</c>, no <c>SUBSEQUENT_BYTES</c>.</item>
///   <item>No alpha plane (<c>ALPHA_IMAGE_PLANE_FLAG=false</c>).</item>
///   <item>Spatial mode (<c>FREQUENCY_MODE_CODESTREAM_FLAG=false</c>).</item>
///   <item><see cref="JxrBandsPresent.AllBands"/> still rejected by
///         <see cref="TileSpatial"/> (FlexBits refinement is pending).</item>
/// </list>
/// <para>The width/height in pixels comes from <see cref="ImageHeader"/>;
/// the macroblock-grid dimensions are derived as <c>ceil(width/16) ×
/// ceil(height/16)</c>. Bottom-right macroblocks at the image edge are
/// padded to 16×16 by the upstream transform pipeline.</para>
/// </remarks>
public sealed class CodedImage
{
    public required ImageHeader ImageHeader { get; init; }
    public required ImagePlaneHeader PlaneHeader { get; init; }
    public required ProfileLevelInfo ProfileLevelInfo { get; init; }
    public required Macroblock[] Macroblocks { get; init; }

    /// <summary>Pixel width (= <c>ImageHeader.WidthMinus1 + 1</c>).</summary>
    public int Width => (int)(ImageHeader.WidthMinus1 + 1);

    /// <summary>Pixel height (= <c>ImageHeader.HeightMinus1 + 1</c>).</summary>
    public int Height => (int)(ImageHeader.HeightMinus1 + 1);

    /// <summary>Macroblock-grid width — <c>ceil(Width / 16)</c>.</summary>
    public int WidthInMb => (Width + 15) >> 4;

    /// <summary>Macroblock-grid height — <c>ceil(Height / 16)</c>.</summary>
    public int HeightInMb => (Height + 15) >> 4;

    /// <summary>
    /// Serialise the codestream: <c>IMAGE_HEADER → IMAGE_PLANE_HEADER →
    /// PROFILE_LEVEL_INFO → TILE_SPATIAL</c>. Returns the byte sequence
    /// suitable for embedding inside a JXR container (<see cref="JxrContainer"/>).
    /// </summary>
    public byte[] Encode()
    {
        ValidateForEncode();

        var writer = new BitWriter();
        ImageHeader.Write(writer);
        PlaneHeader.Write(writer, ImageHeader.OutputBitDepth);
        ProfileLevelInfo.Write(writer);

        TileSpatial.Write(
            writer,
            TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
            PlaneHeader.BandsPresent,
            ImageHeader.TrimFlexBitsFlag,
            PlaneHeader.InternalClrFmt,
            PlaneHeader.NumComponents,
            WidthInMb,
            HeightInMb,
            Macroblocks);

        return writer.ToArray();
    }

    /// <summary>Deserialise the codestream produced by <see cref="Encode"/>.</summary>
    public static CodedImage Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new BitReader(bytes);
        var img = ImageHeader.Read(ref reader);
        if (img.AlphaImagePlaneFlag)
            throw new NotSupportedException("CodedImage.Decode: alpha plane not yet supported");
        if (img.FrequencyModeCodestreamFlag)
            throw new NotSupportedException("CodedImage.Decode: frequency-mode codestream not yet supported");

        var plane = ImagePlaneHeader.Read(ref reader, img.OutputBitDepth);
        var profile = ProfileLevelInfo.Read(ref reader);

        var width = (int)(img.WidthMinus1 + 1);
        var height = (int)(img.HeightMinus1 + 1);
        var widthInMb = (width + 15) >> 4;
        var heightInMb = (height + 15) >> 4;

        var mbs = TileSpatial.Read(
            ref reader,
            plane.BandsPresent,
            img.TrimFlexBitsFlag,
            plane.InternalClrFmt,
            plane.NumComponents,
            widthInMb,
            heightInMb,
            out _);

        return new CodedImage
        {
            ImageHeader = img,
            PlaneHeader = plane,
            ProfileLevelInfo = profile,
            Macroblocks = mbs,
        };
    }

    private void ValidateForEncode()
    {
        if (ImageHeader.AlphaImagePlaneFlag)
            throw new NotSupportedException("CodedImage.Encode: alpha plane not yet supported (set AlphaImagePlaneFlag = false)");
        if (ImageHeader.FrequencyModeCodestreamFlag)
            throw new NotSupportedException("CodedImage.Encode: frequency-mode codestream not yet supported");
        if (Macroblocks.Length != WidthInMb * HeightInMb)
            throw new InvalidOperationException(
                $"Macroblocks has length {Macroblocks.Length}, expected {WidthInMb * HeightInMb} " +
                $"({WidthInMb}×{HeightInMb}) for image {Width}×{Height}");
    }
}
