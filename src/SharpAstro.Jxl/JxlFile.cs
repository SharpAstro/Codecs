namespace SharpAstro.Jxl;

/// <summary>Basic JPEG XL image properties read from the codestream header.</summary>
public readonly record struct JxlImageInfo(
    int Width,
    int Height,
    int BitsPerSample,
    bool IsFloat,
    bool HasAlpha,
    bool IsGrayscale)
{
    /// <summary>
    /// EXIF-style orientation 1 to 8 the codestream asks for (1 = as stored). <see cref="Width"/> and
    /// <see cref="Height"/> are the stored size; 5 to 8 swap them for display, and
    /// <c>JxlImageCodec.Decode</c> returns the image already oriented.
    /// </summary>
    public int Orientation { get; init; } = 1;

    /// <summary>The nits sample value 1.0 is meant to reach (<c>tone_mapping.intensity_target</c>; 255 by default).</summary>
    public float IntensityTarget { get; init; } = 255f;
}

/// <summary>Entry points for reading JPEG XL (.jxl) files.</summary>
public static class JxlFile
{
    /// <summary>
    /// Reads image header properties from a JPEG XL file — either a bare codestream or an
    /// ISOBMFF container.
    /// </summary>
    public static JxlImageInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        byte[] codestream = JxlContainer.ExtractCodestream(data);
        ReadOnlySpan<byte> span = codestream;

        // The codestream begins with the 0xFF 0x0A signature; the headers follow.
        if (span.Length < 2 || span[0] != 0xFF || span[1] != 0x0A)
            throw new InvalidDataException("JPEG XL codestream is missing the 0xFF 0x0A signature.");

        var br = new JxlBitReader(span[2..]);
        (int width, int height) = JxlSizeHeader.Read(ref br);
        JxlImageMetadata meta = JxlImageMetadata.Read(ref br);

        return new JxlImageInfo(
            width,
            height,
            meta.BitDepth.BitsPerSample,
            meta.BitDepth.FloatingPoint,
            meta.HasAlpha,
            meta.ColorSpace == JxlColorSpace.Gray)
        {
            Orientation = meta.Orientation,
            IntensityTarget = meta.IntensityTarget,
        };
    }

    /// <inheritdoc cref="ReadInfo(ReadOnlySpan{byte})"/>
    public static JxlImageInfo ReadInfo(byte[] data) => ReadInfo(data.AsSpan());
}
