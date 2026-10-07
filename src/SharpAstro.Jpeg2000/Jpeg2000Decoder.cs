using System;
using System.IO;

namespace SharpAstro.Jpeg2000;

/// <summary>
/// A decoded JPEG 2000 image: unsigned samples for every component, with the
/// precision the codestream declared.
/// <para>
/// The components are whatever the codestream coded, after the inverse
/// multiple component transform when COD asked for one: so three components
/// coded with RCT come back as the three the encoder started from,
/// conventionally red, green and blue. What they mean is said by the JP2
/// <c>colr</c> box or the PDF image dictionary, not by the codestream, and this
/// type does not guess.
/// </para>
/// </summary>
/// <param name="Width">Samples across.</param>
/// <param name="Height">Samples down.</param>
/// <param name="BitDepth">Bits per sample, 1 to 16, the same for every component.</param>
/// <param name="Samples">
/// Row-major samples, the components of each pixel together
/// (<c>Samples[(y * Width + x) * Components + c]</c>), already DC-level-shifted and
/// clamped to the declared precision.
/// </param>
/// <param name="Components">Samples per pixel.</param>
public sealed record Jpeg2000Image(int Width, int Height, int BitDepth, ushort[] Samples, int Components = 1)
{
    /// <summary>
    /// The samples projected to 8 bits, for callers that just want to look at
    /// a one-component picture. A precision above 8 is shifted down, never
    /// rescaled, so the mapping stays exact and reversible for the common 8-bit
    /// case.
    /// </summary>
    /// <exception cref="InvalidOperationException">The image has more than one component; use <see cref="To8Bit"/>.</exception>
    public byte[] ToGray8()
    {
        if (Components != 1)
            throw new InvalidOperationException(
                $"This image has {Components} components, so it has no single grey plane. Use To8Bit for " +
                "every component, and decide from the container or the PDF what they mean.");

        return To8Bit();
    }

    /// <summary>
    /// Every component projected to 8 bits, interleaved as <see cref="Samples"/> is. A precision
    /// above 8 is shifted down, never rescaled, as in <see cref="ToGray8"/>.
    /// </summary>
    public byte[] To8Bit()
    {
        var shift = Math.Max(0, BitDepth - 8);
        var bytes = new byte[Samples.Length];
        for (var i = 0; i < Samples.Length; i++) bytes[i] = (byte)(Samples[i] >> shift);

        return bytes;
    }
}

/// <summary>
/// A pure-managed JPEG 2000 decoder, clean-room from ITU-T T.800 (ISO/IEC
/// 15444-1, Part 1).
/// <para>
/// <b>What this decodes today.</b> A raw J2K codestream holding any number of
/// 1-to-16-bit unsigned components of one precision, in a single tile, at full
/// resolution, coded in any number of quality layers with the reversible 5/3
/// wavelet, maximal precincts and LRCP progression, with or without the
/// reversible component transform (RCT). That is rung 1 of <c>ROADMAP-jpx.md</c>
/// and the first part of rung 2, with quality layers brought forward from rung 3
/// because every <c>/JPXDecode</c> image measured in real PDFs has several.
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

        var budget = new Jpeg2000SampleBudget(
            Jpeg2000Limits.BudgetFor(siz.Width, siz.Height, siz.Components.Length));

        var components = new TileComponent[siz.Components.Length];
        for (var c = 0; c < components.Length; c++) components[c] = TileComponent.Build(header, c, budget);

        var part = header.TileParts[0];
        var tilePartData = data.Slice(part.Start, part.Length);

        // Tier-2 first, over the whole tile-part: it discovers which code-blocks
        // carry data and where, without decoding any of it. The components'
        // packets are interleaved in the tile-part, so it reads them all at once.
        Tier2.ReadPackets(header, components, tilePartData);

        // Then tier-1, block by block, and the inverse wavelet, component by
        // component.
        var planes = new int[components.Length][];
        for (var c = 0; c < components.Length; c++)
        {
            var tile = components[c];
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
            planes[c] = InverseWavelet.Reconstruct(tile);
        }

        // G.1.2: the component transform comes between the wavelet and the
        // level shift. The reader has already checked that components 0 to 2
        // share one wavelet, and the 5/3 one is the only wavelet it accepts.
        if (header.MultipleComponentTransform) ComponentTransform.InverseRct(planes[0], planes[1], planes[2]);

        return LevelShift(planes, components[0].Bounds, siz.Components[0].BitDepth);
    }

    /// <summary>
    /// T.800 G.1.2: undo the DC level shift the encoder applied to make an
    /// unsigned component signed, then clamp to the declared precision, and
    /// interleave the components into one sample per component per pixel.
    /// <para>
    /// The clamp is not belt-and-braces. A lossless codestream reconstructs
    /// exactly and needs none, but a truncated or hostile one can produce
    /// coefficients outside the range the component claims, and writing those
    /// into a <see cref="ushort"/> unchecked would wrap a bright sample to a
    /// dark one.
    /// </para>
    /// </summary>
    private static Jpeg2000Image LevelShift(int[][] planes, Rect bounds, int bitDepth)
    {
        var shift = 1 << (bitDepth - 1);
        var maximum = (1 << bitDepth) - 1;
        var count = planes.Length;

        var output = new ushort[(long)bounds.Width * bounds.Height * count];
        for (var c = 0; c < count; c++)
        {
            var plane = planes[c];
            for (int i = 0, o = c; i < plane.Length; i++, o += count)
            {
                output[o] = (ushort)Math.Clamp(plane[i] + shift, 0, maximum);
            }
        }

        return new Jpeg2000Image(bounds.Width, bounds.Height, bitDepth, output, count);
    }
}
