using SharpAstro.Pnm;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The shape the JPEG 2000 tests read PNM through, now a thin wrapper over
/// <see cref="PnmReader"/> (the package this started out as a private stand-in for).
/// <para>
/// It stays a wrapper rather than a straight rename because those tests speak of
/// <c>Components</c> (JPEG 2000's word) where the reader says channels, and because
/// <c>opj_decompress</c> stamps a comment line into the header it writes
/// (<c>P5\n#OpenJPEG-2.5.4\n…</c>), so a decoded <c>.pgm</c> is never byte-identical to
/// the source <c>.pgm</c> even when every pixel matches: the comparison has to be on parsed
/// payloads, which is what <see cref="PnmReader"/> skips the comments for.
/// </para>
/// </summary>
internal static class Pnm
{
    /// <summary>A decoded PNM: native interleaved samples, <see cref="Components"/> per pixel.</summary>
    /// <param name="Width">Pixels across.</param>
    /// <param name="Height">Pixels down.</param>
    /// <param name="Components">Samples per pixel (1 for grey, 3 for RGB).</param>
    /// <param name="MaxValue">Header maxval; above 255 the file's samples are 16-bit.</param>
    /// <param name="Samples">Interleaved native samples, row-major.</param>
    internal sealed record Image(int Width, int Height, int Components, int MaxValue, ushort[] Samples);

    /// <summary>Reads a PNM file.</summary>
    public static Image Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Reads a PNM from memory.</summary>
    public static Image Parse(ReadOnlySpan<byte> bytes)
    {
        var image = PnmReader.Read(bytes);
        return new Image(image.Width, image.Height, image.Channels, image.MaxValue, image.Samples);
    }
}
