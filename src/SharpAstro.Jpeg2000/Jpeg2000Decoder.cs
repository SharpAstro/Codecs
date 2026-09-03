using System;
using System.IO;

namespace SharpAstro.Jpeg2000;

/// <summary>
/// A decoded JPEG 2000 image: one plane of unsigned samples, with the precision
/// the codestream declared.
/// </summary>
/// <param name="Width">Samples across.</param>
/// <param name="Height">Samples down.</param>
/// <param name="BitDepth">Bits per sample, 1 to 16.</param>
/// <param name="Samples">Row-major samples, already DC-level-shifted and clamped to the declared precision.</param>
public sealed record Jpeg2000Image(int Width, int Height, int BitDepth, ushort[] Samples)
{
    /// <summary>
    /// The samples projected to 8 bits, for callers that just want to look at
    /// the picture. A precision above 8 is shifted down, never rescaled, so the
    /// mapping stays exact and reversible for the common 8-bit case.
    /// </summary>
    public byte[] ToGray8()
    {
        var shift = Math.Max(0, BitDepth - 8);
        var gray = new byte[Samples.Length];
        for (var i = 0; i < Samples.Length; i++) gray[i] = (byte)(Samples[i] >> shift);

        return gray;
    }
}

/// <summary>
/// A pure-managed JPEG 2000 decoder, clean-room from ITU-T T.800 (ISO/IEC
/// 15444-1, Part 1).
/// <para>
/// <b>What this decodes today.</b> A raw J2K codestream holding one 8-to-16-bit
/// unsigned component in a single tile, coded as one quality layer with the
/// reversible 5/3 wavelet, maximal precincts and LRCP progression. That is
/// rung 1 of <c>ROADMAP-jpx.md</c>: the whole pipeline, deliberately narrow.
/// Anything outside it raises <see cref="NotSupportedException"/> naming the
/// feature and the rung that owns it — never a plausible-looking wrong raster.
/// </para>
/// <para>
/// <b>Why there is no partial-credit mode.</b> JPEG 2000 has no early pipeline
/// stage that produces an image on its own: marker parsing, tier-2 packet
/// headers, tier-1 EBCOT, the inverse DWT and the DC level shift must all be
/// right before a single correct pixel exists. So the staging is by feature, not
/// by stage, and a codestream either falls inside the envelope or is refused at
/// the header.
/// </para>
/// </summary>
public static class Jpeg2000Decoder
{
    /// <summary>
    /// True when <paramref name="data"/> begins with a raw JPEG 2000 codestream
    /// (SOC followed by SIZ).
    /// </summary>
    public static bool IsCodestream(ReadOnlySpan<byte> data) => CodestreamReader.LooksLikeCodestream(data);

    /// <summary>Decodes a raw JPEG 2000 codestream.</summary>
    /// <exception cref="InvalidDataException">The codestream is malformed, truncated or over the resource limits.</exception>
    /// <exception cref="NotSupportedException">It is well-formed but uses a feature this decoder does not implement.</exception>
    public static Jpeg2000Image Decode(ReadOnlySpan<byte> data)
    {
        var header = CodestreamReader.Read(data);
        var siz = header.Siz;
        var component = siz.Components[0];

        var budget = new Jpeg2000SampleBudget(
            Jpeg2000Limits.BudgetFor(siz.Width, siz.Height, siz.Components.Length));

        var tile = TileComponent.Build(header, budget);

        var part = header.TileParts[0];
        var tilePartData = data.Slice(part.Start, part.Length);

        // Tier-2 first, over the whole tile-part: it discovers which code-blocks
        // carry data and where, without decoding any of it.
        Tier2.ReadPackets(header, tile, tilePartData);

        // Then tier-1, block by block.
        foreach (var resolution in tile.Resolutions)
        {
            foreach (var band in resolution.Bands)
            {
                foreach (var block in band.Blocks)
                {
                    BlockDecoder.Decode(band, block, tilePartData);
                }
            }
        }

        budget.Charge(tile.Bounds.Width, tile.Bounds.Height);
        var samples = InverseWavelet.Reconstruct(tile);

        return LevelShift(samples, tile.Bounds, component);
    }

    /// <summary>
    /// T.800 G.1.2: undo the DC level shift the encoder applied to make an
    /// unsigned component signed, then clamp to the declared precision.
    /// <para>
    /// The clamp is not belt-and-braces. A lossless codestream reconstructs
    /// exactly and needs none, but a truncated or hostile one can produce
    /// coefficients outside the range the component claims, and writing those
    /// into a <see cref="ushort"/> unchecked would wrap a bright sample to a
    /// dark one.
    /// </para>
    /// </summary>
    private static Jpeg2000Image LevelShift(int[] samples, Rect bounds, ComponentInfo component)
    {
        var shift = 1 << (component.BitDepth - 1);
        var maximum = (1 << component.BitDepth) - 1;

        var output = new ushort[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            output[i] = (ushort)Math.Clamp(samples[i] + shift, 0, maximum);
        }

        return new Jpeg2000Image(bounds.Width, bounds.Height, component.BitDepth, output);
    }
}
