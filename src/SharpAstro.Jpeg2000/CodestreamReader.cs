using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace SharpAstro.Jpeg2000;

/// <summary>
/// One tile-part's coded data: where its bitstream begins and ends inside the
/// codestream (T.800 A.4.2).
/// </summary>
/// <param name="TileIndex">Isot — which tile this part belongs to.</param>
/// <param name="PartIndex">TPsot — this part's position in that tile's sequence.</param>
/// <param name="Start">Offset of the first byte after SOD.</param>
/// <param name="Length">Bytes of packet data in this part.</param>
internal readonly record struct TilePart(int TileIndex, int PartIndex, int Start, int Length);

/// <summary>
/// Everything the main header said, plus where each tile-part's data lives.
/// </summary>
internal sealed record CodestreamHeader(
    SizMarker Siz,
    CodingStyle Cod,
    Quantization Qcd,
    int Layers,
    ProgressionOrder Progression,
    bool MultipleComponentTransform,
    bool UseSopMarkers,
    bool UseEphMarkers,
    List<TilePart> TileParts);

/// <summary>
/// Walks a raw JPEG 2000 codestream (T.800 Annex A), parsing the main header
/// markers and locating each tile-part's coded data.
/// <para>
/// This layer does no entropy decoding. It exists so that a codestream outside
/// the implemented envelope is refused <b>here</b>, by name, before any of the
/// pipeline runs — the discipline the rest of this repo already holds to, and
/// the reason <c>SharpAstro.Jbig2</c> throws <see cref="NotSupportedException"/>
/// naming SDHUFF rather than returning a plausible-looking raster.
/// </para>
/// <para>
/// The distinction kept throughout: <see cref="InvalidDataException"/> means the
/// bytes are malformed or hostile, <see cref="NotSupportedException"/> means
/// they are a perfectly legal codestream using a feature this rung does not
/// implement yet. A caller can reasonably retry the second with another decoder
/// and cannot do anything with the first.
/// </para>
/// </summary>
internal static class CodestreamReader
{
    /// <summary>The raw-J2K magic: SOC immediately followed by SIZ.</summary>
    public static bool LooksLikeCodestream(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51;

    /// <summary>Parses the main header and indexes the tile-parts.</summary>
    /// <exception cref="InvalidDataException">The codestream is malformed.</exception>
    /// <exception cref="NotSupportedException">It uses a feature this decoder does not implement.</exception>
    public static CodestreamHeader Read(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeCodestream(data))
            throw new InvalidDataException(
                "JPEG 2000: not a raw codestream — it does not begin with SOC (FF4F) followed by SIZ (FF51).");

        var offset = 2; // past SOC
        SizMarker? siz = null;
        CodingStyle? cod = null;
        Quantization? qcd = null;
        var layers = 0;
        var progression = ProgressionOrder.Lrcp;
        var mct = false;
        var sop = false;
        var eph = false;
        var tileParts = new List<TilePart>();

        while (offset < data.Length)
        {
            var marker = ReadUInt16(data, ref offset, "marker");
            if (marker == Markers.Eoc) break;

            if (marker == Markers.Sod)
                throw new InvalidDataException("JPEG 2000: SOD outside a tile-part.");

            if (marker == Markers.Sot)
            {
                ReadTilePart(data, ref offset, tileParts);
                continue;
            }

            // Every other marker in the main header carries a 16-bit length that
            // COUNTS ITSELF, so the segment body is length - 2 bytes.
            var segmentStart = offset;
            var length = ReadUInt16(data, ref offset, "marker segment length");
            if (length < 2 || segmentStart + length > data.Length)
                throw new InvalidDataException(
                    $"JPEG 2000: marker {marker:X4} at offset {segmentStart - 2} declares a {length}-byte " +
                    "segment that runs past the end of the codestream.");

            var body = data.Slice(offset, length - 2);
            offset = segmentStart + length;

            switch (marker)
            {
                case Markers.Siz:
                    siz = ParseSiz(body);
                    break;

                case Markers.Cod:
                    (cod, layers, progression, mct, sop, eph) = ParseCod(body);
                    break;

                case Markers.Qcd:
                    qcd = ParseQcd(body);
                    break;

                // Deliberately skipped: pure indexes and metadata that change
                // nothing about how the coded data is read.
                case Markers.Com:
                case Markers.Tlm:
                case Markers.Plm:
                case Markers.Plt:
                case Markers.Crg:
                    break;

                case Markers.Coc:
                case Markers.Qcc:
                    throw new NotSupportedException(
                        "JPEG 2000: per-component coding or quantization overrides (COC / QCC) are not " +
                        "implemented. They belong to rung 3, with the rest of tier-2; this rung decodes a " +
                        "single component, for which an override has nothing to override.");

                case Markers.Poc:
                    throw new NotSupportedException(
                        "JPEG 2000: progression order changes (POC) are not implemented.");

                case Markers.Ppm:
                    throw new NotSupportedException(
                        "JPEG 2000: packed packet headers (PPM) are not implemented. The packet headers live " +
                        "in a separate marker segment rather than ahead of their packets, so ignoring this " +
                        "would desynchronise tier-2 rather than merely lose a feature.");

                case Markers.Rgn:
                    throw new NotSupportedException(
                        "JPEG 2000: regions of interest (RGN / MAXSHIFT) are refused. PDF does not require " +
                        "them, and a decoder that ignored the marker would silently reconstruct the " +
                        "region-of-interest coefficients at the wrong magnitude.");

                default:
                    throw new NotSupportedException(
                        $"JPEG 2000: unrecognised marker {marker:X4} in the main header. Refusing rather " +
                        "than skipping it: a marker this decoder does not know may change how the bytes " +
                        "after it are to be read.");
            }
        }

        if (siz is null) throw new InvalidDataException("JPEG 2000: no SIZ marker.");
        if (cod is null) throw new InvalidDataException("JPEG 2000: no COD marker.");
        if (qcd is null) throw new InvalidDataException("JPEG 2000: no QCD marker.");
        if (tileParts.Count == 0) throw new InvalidDataException("JPEG 2000: no tile-part data (no SOT).");

        var header = new CodestreamHeader(siz, cod, qcd, layers, progression, mct, sop, eph, tileParts);
        Validate(header);
        return header;
    }

    /// <summary>
    /// Reads one SOT segment and the tile-part data that follows its SOD.
    /// </summary>
    private static void ReadTilePart(ReadOnlySpan<byte> data, ref int offset, List<TilePart> tileParts)
    {
        var sotStart = offset - 2;
        var length = ReadUInt16(data, ref offset, "SOT length");
        if (length != 10)
            throw new InvalidDataException($"JPEG 2000: SOT declares Lsot={length}, which must be 10.");

        var tileIndex = ReadUInt16(data, ref offset, "Isot");
        var psot = (int)ReadUInt32(data, ref offset, "Psot");
        var partIndex = data[offset++];
        offset++; // TNsot — how many parts this tile has; unused while multi-part is refused.

        // Psot counts from the first byte of the SOT MARKER, not from here. Zero
        // is legal and means "to the end of the codestream, or to the next SOT",
        // which only the last tile-part may use (A.4.2).
        var tilePartEnd = psot == 0 ? data.Length : sotStart + psot;
        if (tilePartEnd > data.Length || tilePartEnd <= offset)
            throw new InvalidDataException(
                $"JPEG 2000: tile-part at offset {sotStart} declares Psot={psot}, which does not fit the " +
                "codestream.");

        // Between SOT and SOD sit the tile-part header markers. Rung 1 decodes a
        // single-tile-part codestream whose coding style is entirely in the main
        // header, so anything here is out of envelope and is named as such.
        while (true)
        {
            var marker = ReadUInt16(data, ref offset, "tile-part header marker");
            if (marker == Markers.Sod) break;

            var segmentStart = offset;
            var segmentLength = ReadUInt16(data, ref offset, "tile-part marker segment length");
            if (segmentLength < 2 || segmentStart + segmentLength > data.Length)
                throw new InvalidDataException(
                    $"JPEG 2000: marker {marker:X4} in a tile-part header declares a {segmentLength}-byte " +
                    "segment that runs past the end of the codestream.");
            offset = segmentStart + segmentLength;

            switch (marker)
            {
                case Markers.Com:
                case Markers.Plt:
                    break;

                case Markers.Ppt:
                    throw new NotSupportedException(
                        "JPEG 2000: packed packet headers (PPT) are not implemented.");

                case Markers.Cod:
                case Markers.Coc:
                case Markers.Qcd:
                case Markers.Qcc:
                case Markers.Poc:
                case Markers.Rgn:
                    throw new NotSupportedException(
                        $"JPEG 2000: marker {marker:X4} in a tile-part header overrides the main header for " +
                        "one tile, which is not implemented.");

                default:
                    throw new NotSupportedException(
                        $"JPEG 2000: unrecognised marker {marker:X4} in a tile-part header.");
            }
        }

        tileParts.Add(new TilePart(tileIndex, partIndex, offset, tilePartEnd - offset));
        offset = tilePartEnd;
    }

    private static SizMarker ParseSiz(ReadOnlySpan<byte> body)
    {
        // Rsiz(2) X1(4) Y1(4) X0(4) Y0(4) XT(4) YT(4) XT0(4) YT0(4) C(2) = 36
        if (body.Length < 36)
            throw new InvalidDataException("JPEG 2000: SIZ segment is too short.");

        var offset = 2; // Rsiz — capabilities; nothing here depends on it.
        var x1 = (int)ReadUInt32(body, ref offset, "Xsiz");
        var y1 = (int)ReadUInt32(body, ref offset, "Ysiz");
        var x0 = (int)ReadUInt32(body, ref offset, "XOsiz");
        var y0 = (int)ReadUInt32(body, ref offset, "YOsiz");
        var tileWidth = (int)ReadUInt32(body, ref offset, "XTsiz");
        var tileHeight = (int)ReadUInt32(body, ref offset, "YTsiz");
        var tileX0 = (int)ReadUInt32(body, ref offset, "XTOsiz");
        var tileY0 = (int)ReadUInt32(body, ref offset, "YTOsiz");
        var componentCount = ReadUInt16(body, ref offset, "Csiz");

        // These are 32-bit fields read into int, so the sign is the first thing
        // to check: a codestream declaring Xsiz = 0x80000000 would otherwise
        // arrive as a negative width and sail through every `>` bound below.
        if (x0 < 0 || y0 < 0 || x1 <= x0 || y1 <= y0)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares an empty or out-of-range image region ({x0},{y0})-({x1},{y1}).");
        if (tileWidth <= 0 || tileHeight <= 0 || tileX0 < 0 || tileY0 < 0 || tileX0 > x0 || tileY0 > y0)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares an invalid tile grid: origin ({tileX0},{tileY0}), " +
                $"size {tileWidth}x{tileHeight}.");

        if (componentCount is 0 or > Jpeg2000Limits.MaxComponents)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares {componentCount} components; the limit here is " +
                $"{Jpeg2000Limits.MaxComponents}.");

        if (body.Length < offset + componentCount * 3)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares {componentCount} components but is too short to describe them.");

        var components = new ComponentInfo[componentCount];
        for (var c = 0; c < componentCount; c++)
        {
            var ssiz = body[offset++];
            var horizontal = body[offset++];
            var vertical = body[offset++];

            if (horizontal == 0 || vertical == 0)
                throw new InvalidDataException(
                    $"JPEG 2000: component {c} declares a zero subsampling factor.");

            components[c] = new ComponentInfo(
                BitDepth: (ssiz & 0x7F) + 1,
                IsSigned: (ssiz & 0x80) != 0,
                HorizontalSeparation: horizontal,
                VerticalSeparation: vertical);
        }

        return new SizMarker(x0, y0, x1, y1, tileX0, tileY0, tileWidth, tileHeight, components);
    }

    private static (CodingStyle Cod, int Layers, ProgressionOrder Progression, bool Mct, bool Sop, bool Eph)
        ParseCod(ReadOnlySpan<byte> body)
    {
        // Scod(1) ProgOrder(1) Layers(2) MCT(1) Levels(1) xcb(1) ycb(1) style(1) transform(1) = 10
        if (body.Length < 10)
            throw new InvalidDataException("JPEG 2000: COD segment is too short.");

        var scod = body[0];
        var progression = (ProgressionOrder)body[1];
        var layers = BinaryPrimitives.ReadUInt16BigEndian(body[2..]);
        var mct = body[4] != 0;
        var levels = body[5];

        // xcb and ycb are stored biased by 2: the value 4 means 2^6 = 64.
        var codeBlockWidthExponent = (body[6] & 0x0F) + 2;
        var codeBlockHeightExponent = (body[7] & 0x0F) + 2;
        var codeBlockStyle = body[8];
        var transform = (WaveletTransform)body[9];

        if (!Enum.IsDefined(progression))
            throw new InvalidDataException($"JPEG 2000: COD declares progression order {body[1]}, which T.800 does not define.");
        if (!Enum.IsDefined(transform))
            throw new InvalidDataException($"JPEG 2000: COD declares wavelet transform {body[9]}, which T.800 does not define.");
        if (layers == 0)
            throw new InvalidDataException("JPEG 2000: COD declares zero quality layers.");
        if (levels > Jpeg2000Limits.MaxDecompositionLevels)
            throw new InvalidDataException(
                $"JPEG 2000: COD declares {levels} decomposition levels; the limit here is " +
                $"{Jpeg2000Limits.MaxDecompositionLevels}.");

        // T.800 Table A.18: no code-block dimension below 4 or above 1024, and
        // no more than 4096 coefficients in one block.
        if (codeBlockWidthExponent is < 2 or > 10 || codeBlockHeightExponent is < 2 or > 10 ||
            codeBlockWidthExponent + codeBlockHeightExponent > 12)
            throw new InvalidDataException(
                $"JPEG 2000: COD declares a code-block of 2^{codeBlockWidthExponent} x " +
                $"2^{codeBlockHeightExponent}, outside what T.800 Table A.18 permits.");

        var precinctSizes = Array.Empty<byte>();
        if ((scod & 0x01) != 0)
        {
            if (body.Length < 10 + levels + 1)
                throw new InvalidDataException(
                    "JPEG 2000: COD signals custom precincts but does not carry one size per resolution.");
            precinctSizes = body.Slice(10, levels + 1).ToArray();
        }

        var style = new CodingStyle(
            levels,
            codeBlockWidthExponent,
            codeBlockHeightExponent,
            codeBlockStyle,
            transform,
            precinctSizes);

        return (style, layers, progression, mct, (scod & 0x02) != 0, (scod & 0x04) != 0);
    }

    private static Quantization ParseQcd(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1)
            throw new InvalidDataException("JPEG 2000: QCD segment is too short.");

        var sqcd = body[0];
        var style = (QuantizationStyle)(sqcd & 0x1F);
        var guardBits = sqcd >> 5;
        var values = body[1..];

        switch (style)
        {
            case QuantizationStyle.None:
            {
                // One byte per subband; the exponent is the top five bits.
                var exponents = new int[values.Length];
                for (var i = 0; i < values.Length; i++) exponents[i] = values[i] >> 3;
                return new Quantization(style, guardBits, exponents, new int[values.Length]);
            }

            case QuantizationStyle.ScalarDerived:
            case QuantizationStyle.ScalarExpounded:
            {
                if (values.Length % 2 != 0)
                    throw new InvalidDataException("JPEG 2000: QCD step sizes are not a whole number of 16-bit values.");

                var count = values.Length / 2;
                var exponents = new int[count];
                var mantissas = new int[count];
                for (var i = 0; i < count; i++)
                {
                    var packed = BinaryPrimitives.ReadUInt16BigEndian(values[(i * 2)..]);
                    exponents[i] = packed >> 11;
                    mantissas[i] = packed & 0x07FF;
                }

                return new Quantization(style, guardBits, exponents, mantissas);
            }

            default:
                throw new InvalidDataException(
                    $"JPEG 2000: QCD declares quantization style {sqcd & 0x1F}, which T.800 Table A.28 does not define.");
        }
    }

    /// <summary>
    /// Refuses, by name, everything outside the envelope this rung has actually
    /// been validated against. Each message says which rung owns the feature, so
    /// the refusal reads as a roadmap position rather than a dead end.
    /// </summary>
    private static void Validate(CodestreamHeader header)
    {
        var siz = header.Siz;

        if (siz.Components.Length != 1)
            throw new NotSupportedException(
                $"JPEG 2000: this codestream has {siz.Components.Length} components; only single-component " +
                "images are implemented. Multiple components arrive with RCT/ICT and the 9/7 filter, which " +
                "is rung 2 — decoding the planes independently and ignoring the component transform would " +
                "produce a picture in the wrong colours rather than an error.");

        var component = siz.Components[0];
        if (component.IsSigned)
            throw new NotSupportedException(
                "JPEG 2000: signed components are not implemented.");
        if (component.BitDepth is < 1 or > 16)
            throw new NotSupportedException(
                $"JPEG 2000: {component.BitDepth}-bit components are not implemented; the limit here is 16.");
        if (component.HorizontalSeparation != 1 || component.VerticalSeparation != 1)
            throw new NotSupportedException(
                "JPEG 2000: component subsampling is not implemented.");

        if (siz.TileCount != 1)
            throw new NotSupportedException(
                $"JPEG 2000: this codestream has {siz.TileCount} tiles; only single-tile images are " +
                "implemented. Multiple tiles are rung 3.");
        if (header.TileParts.Count != 1)
            throw new NotSupportedException(
                $"JPEG 2000: this tile is split into {header.TileParts.Count} tile-parts, which is rung 3.");

        if (header.Layers != 1)
            throw new NotSupportedException(
                $"JPEG 2000: this codestream has {header.Layers} quality layers; only a single layer is " +
                "implemented. Multiple layers are rung 3, and they are not merely more of the same — a " +
                "precinct's tag trees carry state ACROSS layers, so a decoder that handles one layer says " +
                "nothing about whether it handles two.");

        if (header.Progression != ProgressionOrder.Lrcp)
            throw new NotSupportedException(
                $"JPEG 2000: progression order {header.Progression} is not implemented; only LRCP is. " +
                "With one tile, one layer and one component the five orders visit the same packets in the " +
                "same sequence, so accepting another would be untested rather than free.");

        if (header.Cod.Transform != WaveletTransform.Reversible53)
            throw new NotSupportedException(
                "JPEG 2000: the 9/7 irreversible wavelet is not implemented; only the reversible 5/3 filter " +
                "is. The 9/7 path is rung 2, and it needs dequantisation from QCD's exponent/mantissa pairs " +
                "and a tolerance-based test rather than the exact one the reversible path gets.");

        if (header.Qcd.Style != QuantizationStyle.None)
            throw new NotSupportedException(
                $"JPEG 2000: quantization style {header.Qcd.Style} is not implemented. Only the reversible " +
                "path's unquantized coefficients are, which is rung 1's envelope.");

        if (header.Cod.CodeBlockStyle != 0)
            throw new NotSupportedException(
                $"JPEG 2000: code-block style flags 0x{header.Cod.CodeBlockStyle:X2} are not implemented " +
                "(T.800 Table A.19: selective arithmetic bypass, context reset, termination per pass, " +
                "vertically causal context, predictable termination, segmentation symbols). Each changes " +
                "how the coded bytes are read, so ignoring one desynchronises tier-1 rather than degrading " +
                "quality.");

        if (header.UseSopMarkers || header.UseEphMarkers)
            throw new NotSupportedException(
                "JPEG 2000: SOP and EPH markers are not implemented; they are rung 3.");

        if (header.Cod.PrecinctSizes.Length != 0)
            throw new NotSupportedException(
                "JPEG 2000: custom precinct sizes are not implemented; only the maximal default, which " +
                "makes each resolution a single precinct. Precincts are rung 3.");

        if (header.MultipleComponentTransform)
            throw new NotSupportedException(
                "JPEG 2000: the multiple component transform (RCT/ICT) is not implemented; it is rung 2.");
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int offset, string what)
    {
        if (offset + 2 > data.Length)
            throw new InvalidDataException($"JPEG 2000: codestream ends inside {what}.");

        var value = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset, string what)
    {
        if (offset + 4 > data.Length)
            throw new InvalidDataException($"JPEG 2000: codestream ends inside {what}.");

        var value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;
        return value;
    }
}
