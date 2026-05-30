namespace SharpAstro.Jxl;

/// <summary>Basic JPEG XL image properties read from the codestream header.</summary>
public readonly record struct JxlImageInfo(int Width, int Height);

/// <summary>Entry points for reading JPEG XL (.jxl) files.</summary>
public static class JxlFile
{
    /// <summary>
    /// Reads the image dimensions from a JPEG XL file — either a bare codestream or an
    /// ISOBMFF container.
    /// </summary>
    public static JxlImageInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        byte[] codestream = JxlContainer.ExtractCodestream(data);
        ReadOnlySpan<byte> span = codestream;

        // The codestream begins with the 0xFF 0x0A signature; the SizeHeader follows.
        if (span.Length < 2 || span[0] != 0xFF || span[1] != 0x0A)
            throw new InvalidDataException("JPEG XL codestream is missing the 0xFF 0x0A signature.");

        var br = new JxlBitReader(span[2..]);
        (int width, int height) = JxlSizeHeader.Read(ref br);
        return new JxlImageInfo(width, height);
    }

    /// <inheritdoc cref="ReadInfo(ReadOnlySpan{byte})"/>
    public static JxlImageInfo ReadInfo(byte[] data) => ReadInfo(data.AsSpan());
}
