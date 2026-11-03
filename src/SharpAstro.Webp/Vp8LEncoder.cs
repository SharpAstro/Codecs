namespace SharpAstro.Webp;

// Provenance: spec-derived from the prose and tables of RFC 9649 only. No libwebp, libvpx or
// RFC 6386 section 20 source was read or transcribed; cwebp and dwebp are used only as black-box
// test oracles.

/// <summary>
/// The simplest VP8L encoder rung (RFC 9649 sections 3.3, 3.5-3.7): literal-only prefix codes, no
/// transforms, no backward references, no colour cache and no meta prefix codes. One prefix-code
/// group of five codes is built from the whole image's symbol histograms.
/// </summary>
internal static class Vp8LEncoder
{
    /// <summary>The VP8L signature byte (RFC 9649 section 3.2).</summary>
    public const byte Signature = 0x2f;

    // Green codes 256 literals, 24 length prefix codes and the colour cache indices (none here);
    // red, blue and alpha code 256 literals; the distance code has 40 prefix symbols.
    private const int GreenAlphabet = 256 + 24;
    private const int ChannelAlphabet = 256;
    private const int DistanceAlphabet = 40;

    /// <summary>
    /// Encodes <paramref name="argb"/> (one 0xAARRGGBB int per pixel, row-major, tightly packed) as
    /// the payload of a <c>VP8L</c> chunk. <paramref name="width"/> and <paramref name="height"/>
    /// must be 1 to 16384.
    /// </summary>
    public static byte[] EncodeLiteral(ReadOnlySpan<int> argb, int width, int height, bool alphaIsUsed)
    {
        long pixelCount = (long)width * height;
        if (argb.Length < pixelCount)
        {
            throw new ArgumentException("The pixel span is shorter than width * height.", nameof(argb));
        }

        int[] green = new int[GreenAlphabet];
        int[] red = new int[ChannelAlphabet];
        int[] blue = new int[ChannelAlphabet];
        int[] alpha = new int[ChannelAlphabet];
        for (int i = 0; i < (int)pixelCount; i++)
        {
            uint p = (uint)argb[i];
            alpha[p >> 24]++;
            red[(p >> 16) & 0xff]++;
            green[(p >> 8) & 0xff]++;
            blue[p & 0xff]++;
        }

        var greenCode = Vp8LPrefixCode.Build(green);
        var redCode = Vp8LPrefixCode.Build(red);
        var blueCode = Vp8LPrefixCode.Build(blue);
        var alphaCode = Vp8LPrefixCode.Build(alpha);
        var distanceCode = Vp8LPrefixCode.Build(new int[DistanceAlphabet]);

        // Rough guess at the size, so a typical image does not regrow its buffer.
        var writer = new Vp8LBitWriter((int)Math.Min(pixelCount * 2 + 256, 1 << 26));

        writer.WriteBits(Signature, 8);
        writer.WriteBits((uint)(width - 1), 14);
        writer.WriteBits((uint)(height - 1), 14);
        writer.WriteBits(alphaIsUsed ? 1u : 0u, 1);
        writer.WriteBits(0, 3); // version

        writer.WriteBits(0, 1); // no transform
        writer.WriteBits(0, 1); // no colour cache
        writer.WriteBits(0, 1); // no meta prefix image: one prefix code group for the whole image

        greenCode.WriteHeader(writer);
        redCode.WriteHeader(writer);
        blueCode.WriteHeader(writer);
        alphaCode.WriteHeader(writer);
        distanceCode.WriteHeader(writer);

        // Each pixel is its four literals in the order green, red, blue, alpha.
        for (int i = 0; i < (int)pixelCount; i++)
        {
            uint p = (uint)argb[i];
            greenCode.WriteSymbol(writer, (int)((p >> 8) & 0xff));
            redCode.WriteSymbol(writer, (int)((p >> 16) & 0xff));
            blueCode.WriteSymbol(writer, (int)(p & 0xff));
            alphaCode.WriteSymbol(writer, (int)(p >> 24));
        }

        return writer.Finish().ToArray();
    }
}
