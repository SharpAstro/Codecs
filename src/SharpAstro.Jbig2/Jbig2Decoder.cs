using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace SharpAstro.Jbig2;

/// <summary>
/// JBIG2 (ITU-T T.88) bilevel decoder, written clean-room from the
/// specification.
/// <para>
/// The primary entry point is <see cref="Decode(ReadOnlySpan{byte}, ReadOnlySpan{byte}, int, int)"/>,
/// shaped for PDF's <c>/JBIG2Decode</c> filter rather than for the facade. A
/// PDF-embedded JBIG2 stream is not a file: T.88 §D.3's "embedded stream"
/// organization has no file header (so there is nothing for
/// <c>IImageDecoder.CanDecode</c> to sniff), its shared segment dictionaries sit
/// in a separate stream referenced by <c>/DecodeParms /JBIG2Globals</c>, and its
/// width and height come from the image dictionary. All three of those are
/// arguments here because none of them can be recovered from the bytes.
/// </para>
/// <para>
/// Standalone <c>.jb2</c> files — which do have a header, and do carry their own
/// dimensions — go through <see cref="DecodeFile"/>, and register with the
/// <c>SharpAstro.Codecs</c> facade via <see cref="Jbig2ImageDecoder"/>.
/// </para>
/// <para>
/// <b>Implemented:</b> the MQ arithmetic decoder (Annex E), generic region
/// decoding both ways — arithmetic (§6.2.5: GBTEMPLATE 0-3, TPGDON, arbitrary AT
/// pixels) and MMR / ITU-T T.6 (§6.2.6) — page information, and region
/// composition. <b>Not yet:</b> symbol dictionary + text region, generic
/// refinement, and halftone regions. Those throw
/// <see cref="NotSupportedException"/> naming the missing feature — a stream this
/// decoder cannot fully reconstruct fails loudly rather than returning a
/// plausible-looking partial page.
/// </para>
/// </summary>
public static class Jbig2Decoder
{
    /// <summary>The 8-byte file header of a standalone <c>.jb2</c> file (T.88 §D.4.1).</summary>
    public static ReadOnlySpan<byte> FileSignature => [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Decodes a PDF-embedded JBIG2 image stream.
    /// </summary>
    /// <param name="embedded">The image XObject's stream data, after any outer filters.</param>
    /// <param name="globals">
    /// The <c>/DecodeParms /JBIG2Globals</c> stream, or empty when the image has none. Its
    /// segments are processed first, exactly as if they preceded
    /// <paramref name="embedded"/>.
    /// </param>
    /// <param name="width">Page width, from the image dictionary's <c>/Width</c>.</param>
    /// <param name="height">Page height, from the image dictionary's <c>/Height</c>.</param>
    /// <exception cref="InvalidDataException">The stream is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">The stream needs a JBIG2 feature this decoder does not implement yet.</exception>
    public static Jbig2Image Decode(ReadOnlySpan<byte> embedded, ReadOnlySpan<byte> globals, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");

        var page = new Jbig2Bitmap(width, height);
        var composited = false;

        // Globals carry segment definitions shared by several images in one PDF;
        // they are processed as a prefix of the image's own segment stream. Any
        // page association is honoured as-is, since a globals stream conventionally
        // uses page 0 ("applies to every page").
        if (!globals.IsEmpty)
            ProcessStream(globals, ReadSequential(globals), page, targetPage: 0, ref composited);

        ProcessStream(embedded, ReadSequential(embedded), page, targetPage: 0, ref composited);

        return new Jbig2Image(width, height, page.Data);
    }

    /// <summary>
    /// Decodes a PDF-embedded JBIG2 image stream that has no
    /// <c>/JBIG2Globals</c>.
    /// </summary>
    public static Jbig2Image Decode(ReadOnlySpan<byte> embedded, int width, int height) =>
        Decode(embedded, default, width, height);

    /// <summary>
    /// Decodes page 1 of a standalone <c>.jb2</c> file (T.88 Annex D), in either
    /// the sequential or the random-access organization.
    /// </summary>
    /// <exception cref="InvalidDataException">The file header or a segment is malformed.</exception>
    /// <exception cref="NotSupportedException">The file needs a JBIG2 feature this decoder does not implement yet.</exception>
    public static Jbig2Image DecodeFile(ReadOnlySpan<byte> file)
    {
        var segments = ReadFile(file, out var body);
        if (!TryFindPageSize(body, segments, out var width, out var height))
            throw new InvalidDataException("JBIG2: file has no page information segment for page 1, or its height is indeterminate.");

        var page = new Jbig2Bitmap(width, height);
        var composited = false;
        ProcessStream(body, segments, page, targetPage: 1, ref composited);

        return new Jbig2Image(width, height, page.Data);
    }

    /// <summary>
    /// Reads page 1's dimensions from a standalone <c>.jb2</c> file without
    /// decoding pixels. False when the header is malformed, the page information
    /// segment is missing, or the page is striped with no end-of-stripe segment
    /// to bound it.
    /// </summary>
    public static bool TryReadFileInfo(ReadOnlySpan<byte> file, out int width, out int height)
    {
        width = height = 0;
        try
        {
            var segments = ReadFile(file, out var body);
            return TryFindPageSize(body, segments, out width, out height);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // ---- file / stream structure -------------------------------------------------

    private static List<SegmentHeader> ReadFile(ReadOnlySpan<byte> file, out ReadOnlySpan<byte> body)
    {
        if (file.Length < 9 || !file[..8].SequenceEqual(FileSignature))
            throw new InvalidDataException("JBIG2: missing file header signature.");

        var flags = file[8];
        var sequential = (flags & 0x01) != 0;
        var pageCountKnown = (flags & 0x02) == 0;

        var offset = 9;
        if (pageCountKnown)
        {
            if (file.Length < 13) throw new InvalidDataException("JBIG2: truncated file header.");
            offset += 4;
        }

        body = file[offset..];
        return sequential ? ReadSequential(body) : ReadRandomAccess(body);
    }

    /// <summary>
    /// Sequential / embedded organization: each segment's data part immediately
    /// follows its header, so <see cref="SegmentHeader.DataStart"/> as parsed is
    /// already correct.
    /// </summary>
    private static List<SegmentHeader> ReadSequential(ReadOnlySpan<byte> stream)
    {
        var segments = new List<SegmentHeader>();
        var position = 0;
        while (position < stream.Length)
        {
            var header = Jbig2Segment.ReadHeader(stream, ref position);
            segments.Add(header);
            position = header.DataStart + header.DataLength;
        }

        return segments;
    }

    /// <summary>
    /// Random-access organization (T.88 §D.2): every segment header first, then
    /// every data part in the same order. The header block ends exactly where the
    /// bytes still unread equal the data lengths declared so far — which is a
    /// precise terminator, not a heuristic, because this decoder rejects
    /// unknown-length segments up front.
    /// </summary>
    private static List<SegmentHeader> ReadRandomAccess(ReadOnlySpan<byte> stream)
    {
        var headers = new List<SegmentHeader>();
        var position = 0;
        long declared = 0;

        while (position + declared < stream.Length)
        {
            var header = Jbig2Segment.ReadHeader(stream, ref position);
            headers.Add(header);
            declared += header.DataLength;
        }

        if (position + declared != stream.Length)
            throw new InvalidDataException("JBIG2: random-access segment headers do not account for the file's data parts.");

        var segments = new List<SegmentHeader>(headers.Count);
        var dataStart = position;
        foreach (var header in headers)
        {
            segments.Add(header with { DataStart = dataStart });
            dataStart += header.DataLength;
        }

        return segments;
    }

    // ---- page geometry -----------------------------------------------------------

    private static bool TryFindPageSize(ReadOnlySpan<byte> stream, List<SegmentHeader> segments, out int width, out int height)
    {
        width = height = 0;

        foreach (var segment in segments)
        {
            if (segment.Type != SegmentType.PageInformation || segment.Page is not (0 or 1)) continue;
            if (segment.DataLength < 19) return false;

            var info = stream.Slice(segment.DataStart, segment.DataLength);
            var w = BinaryPrimitives.ReadUInt32BigEndian(info);
            var h = BinaryPrimitives.ReadUInt32BigEndian(info[4..]);

            // §7.4.8.2: an unknown height means the page is striped, and its real
            // extent is only known from the end-of-stripe segments that follow.
            if (h == uint.MaxValue && !TryFindStripedHeight(stream, segments, out h))
                return false;

            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue || (long)w * h > 1L << 31)
                return false;

            width = (int)w;
            height = (int)h;
            return true;
        }

        return false;
    }

    private static bool TryFindStripedHeight(ReadOnlySpan<byte> stream, List<SegmentHeader> segments, out uint height)
    {
        // §7.4.10: an end-of-stripe segment carries the row number of the stripe's
        // last row, so the tallest one plus one is the page height.
        long last = -1;
        foreach (var segment in segments)
        {
            if (segment.Type != SegmentType.EndOfStripe || segment.DataLength < 4) continue;
            last = Math.Max(last, BinaryPrimitives.ReadUInt32BigEndian(stream.Slice(segment.DataStart, 4)));
        }

        height = (uint)(last + 1);
        return last >= 0;
    }

    // ---- segment dispatch --------------------------------------------------------

    private static void ProcessStream(
        ReadOnlySpan<byte> stream,
        List<SegmentHeader> segments,
        Jbig2Bitmap page,
        uint targetPage,
        ref bool composited)
    {
        foreach (var segment in segments)
        {
            // Page 0 means "not page-specific"; otherwise a file's segments are
            // filtered to the page being decoded. Embedded streams pass
            // targetPage 0 and take everything, since a PDF image is one page by
            // construction and encoders are inconsistent about the field.
            if (targetPage != 0 && segment.Page != 0 && segment.Page != targetPage) continue;

            ProcessSegment(segment, stream.Slice(segment.DataStart, segment.DataLength), page, ref composited);
        }
    }

    private static void ProcessSegment(SegmentHeader segment, ReadOnlySpan<byte> data, Jbig2Bitmap page, ref bool composited)
    {
        switch (segment.Type)
        {
            case SegmentType.PageInformation:
                ApplyPageInformation(data, page, composited);
                break;

            case SegmentType.ImmediateGenericRegion:
            case SegmentType.ImmediateLosslessGenericRegion:
                DecodeGenericRegion(data, page);
                composited = true;
                break;

            case SegmentType.IntermediateGenericRegion:
                // Intermediate regions are not composited onto the page: they are
                // held in an auxiliary buffer for a later refinement region to
                // consume. Nothing can reference one until refinement lands, so
                // decoding it now would be work thrown away.
                break;

            case SegmentType.EndOfPage:
            case SegmentType.EndOfStripe:
            case SegmentType.EndOfFile:
            case SegmentType.Profiles:
            case SegmentType.Extension:
                break;

            case SegmentType.SymbolDictionary:
            case SegmentType.IntermediateTextRegion:
            case SegmentType.ImmediateTextRegion:
            case SegmentType.ImmediateLosslessTextRegion:
            case SegmentType.Tables:
                throw new NotSupportedException(
                    "JBIG2: symbol dictionary / text region segments are not implemented yet.");

            case SegmentType.PatternDictionary:
            case SegmentType.IntermediateHalftoneRegion:
            case SegmentType.ImmediateHalftoneRegion:
            case SegmentType.ImmediateLosslessHalftoneRegion:
                throw new NotSupportedException(
                    "JBIG2: pattern dictionary / halftone region segments are not implemented yet.");

            case SegmentType.IntermediateRefinementRegion:
            case SegmentType.ImmediateRefinementRegion:
            case SegmentType.ImmediateLosslessRefinementRegion:
                throw new NotSupportedException(
                    "JBIG2: generic refinement region segments are not implemented yet.");

            default:
                // §7.2.3: a decoder skips segment types it does not recognise. The
                // data length in the header is what makes that safe.
                break;
        }
    }

    /// <summary>
    /// Page information segment (T.88 §7.4.8). Only the default pixel value has
    /// any effect here: the page's own width and height are overridden by the
    /// caller-supplied dimensions (embedded) or already consumed to size the
    /// bitmap (file).
    /// </summary>
    private static void ApplyPageInformation(ReadOnlySpan<byte> data, Jbig2Bitmap page, bool composited)
    {
        if (data.Length < 19)
            throw new InvalidDataException("JBIG2: truncated page information segment.");

        var flags = data[16];
        var defaultPixel = (flags >> 2) & 1;

        // A conforming stream puts page information before any region, so a late
        // one would be re-painting over decoded content; ignore it rather than
        // erase what has already been composited.
        if (defaultPixel != 0 && !composited)
            page.Data.AsSpan().Fill(1);
    }

    /// <summary>Generic region segment (T.88 §7.4.6): region info, flags, AT pixels, then MQ-coded data.</summary>
    private static void DecodeGenericRegion(ReadOnlySpan<byte> data, Jbig2Bitmap page)
    {
        var position = 0;
        var region = Jbig2Segment.ReadRegionInfo(data, ref position);

        if (position >= data.Length)
            throw new InvalidDataException("JBIG2: truncated generic region segment flags.");

        var flags = data[position++];
        var mmr = (flags & 0x01) != 0;
        var template = (flags >> 1) & 0x03;
        var typicalPrediction = (flags & 0x08) != 0;
        var extTemplate = (flags & 0x10) != 0;

        Jbig2Bitmap bitmap;
        if (mmr)
        {
            // §7.4.6.3: the AT pixel list is present only when MMR is 0, so the
            // coded data starts immediately after the flags byte. GBTEMPLATE and
            // TPGDON are required to be 0 here and mean nothing to T.6 coding —
            // a stream that sets them anyway still decodes to the right pixels,
            // so they are ignored rather than made fatal.
            bitmap = MmrDecoder.Decode(data[position..], region.Width, region.Height);
        }
        else
        {
            if (extTemplate)
                throw new NotSupportedException("JBIG2: EXTTEMPLATE generic regions are not implemented yet.");

            Span<sbyte> at = stackalloc sbyte[8];
            var atPixels = template == 0 ? 4 : 1;
            if (position + atPixels * 2 > data.Length)
                throw new InvalidDataException("JBIG2: truncated generic region AT pixel list.");

            for (var i = 0; i < atPixels; i++)
            {
                at[i * 2] = (sbyte)data[position++];
                at[i * 2 + 1] = (sbyte)data[position++];
            }

            var contexts = new byte[1 << GenericRegionDecoder.ContextBits(template)];
            var mq = new MqDecoder(data[position..]);
            bitmap = GenericRegionDecoder.Decode(
                ref mq, contexts, region.Width, region.Height, template, typicalPrediction, at);
        }

        page.Combine(bitmap, region.X, region.Y, region.Operator);
    }
}
