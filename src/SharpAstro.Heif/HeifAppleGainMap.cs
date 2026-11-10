using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SharpAstro.Heif;

/// <summary>Which carrier a gain map was found in.</summary>
public enum HeifGainMapDialect
{
    /// <summary>An ISO/TS 21496-1 <c>tmap</c> derived image item: exact metadata, every parameter per channel.</summary>
    IsoTmap,

    /// <summary>
    /// Apple's auxiliary image (<c>auxl</c> to the image, <c>auxC</c> <see cref="HeifAppleGainMap.AuxiliaryUrn"/>) with a
    /// single headroom taken from metadata: best-effort (see <see cref="HeifAppleGainMap"/>).
    /// </summary>
    AppleAux,
}

/// <summary>Where an Apple gain map's headroom was read from.</summary>
public enum HeifAppleHeadroomSource
{
    /// <summary>The primary image's Exif <c>MakerNote</c> (Apple's), tags 33 and 48, through Apple's stops formula.</summary>
    MakerNote,

    /// <summary>The auxiliary item's XMP, <c>hdrgm:HDRCapacityMax</c> (log2 of the headroom; the Adobe vocabulary).</summary>
    Xmp,
}

/// <summary>
/// What an Apple-dialect gain map found by <see cref="HeifFile.GainMapOf"/> adds to <see cref="HeifGainMapInfo"/>.
/// <para>
/// <b>Best-effort, from Apple's public description.</b> Everything here comes from Apple's developer article
/// "Applying Apple HDR effect to your photos" (<c>developer.apple.com/documentation/appkit/applying-apple-hdr-effect-to-your-photos</c>)
/// and nothing from libheif, libultrahdr or exiftool source: the map is an 8-bit single-channel auxiliary image of the type
/// <see cref="AuxiliaryUrn"/>, "untagged, formatted as linear data, encoded using the Rec.709 transfer function", a quarter of
/// the image's resolution; the headroom is computed from the <c>MakerNote</c> keys 33 and 48 (32-bit floats) by
/// <see cref="HeadroomFromStops"/>; and the HDR pixel is <c>sdr_rgb * (1.0 + (headroom - 1.0) * gainmap)</c> on linear values.
/// The article is silent on a display with less headroom than the image (see
/// <see cref="SharpAstro.GainMap.GainMapReconstructor.ReconstructAppleFromLinear"/> for what is done) and on how the keys
/// are stored in the raw Exif (here: any of RATIONAL, SRATIONAL, FLOAT, DOUBLE is read). The article's other validity check, the
/// <c>HDRGainMapVersion</c> metadata key, is not required: a map that has the headroom is reconstructed. Not checked against an
/// iPhone file (none is available to this repository's tests); the files in the tests are built to the article's description.
/// </para>
/// </summary>
/// <param name="Aux">The auxiliary image item, the same item as <see cref="HeifGainMapInfo.Map"/>.</param>
/// <param name="Headroom">The headroom, a linear ratio to SDR white, above 1 (and at most <see cref="HeifAppleGainMap.MaxHeadroom"/>).</param>
/// <param name="Source">Where it was read from.</param>
public sealed record HeifAppleGainMap(HeifItem Aux, double Headroom, HeifAppleHeadroomSource Source)
{
    /// <summary>The <c>auxC</c> URN Apple gives its gain-map auxiliary images.</summary>
    public const string AuxiliaryUrn = "urn:com:apple:photo:2020:aux:hdrgainmap";

    /// <summary>
    /// The largest headroom taken as real: 2^8 = 256 (eight stops; phones write under four). A bigger one is a corrupt
    /// or invented value and is refused (<see cref="InvalidDataException"/>) rather than amplifying the base by it.
    /// </summary>
    public const double MaxHeadroom = 256;

    /// <summary>
    /// Apple's conversion of the <c>MakerNote</c> keys to a linear headroom, verbatim from the article:
    /// <code>
    /// if maker33 &lt; 1.0 { stops = maker48 &lt;= 0.01 ? -20.0*maker48 + 1.8   : -0.101*maker48 + 1.601 }
    /// else              { stops = maker48 &lt;= 0.01 ? -70.0*maker48 + 3.0   : -0.303*maker48 + 2.303 }
    /// headroom = 2^max(stops, 0)
    /// </code>
    /// The constants are Apple's. A result of exactly 1 (stops 0 or below) means no HDR effect.
    /// </summary>
    public static double HeadroomFromStops(double maker33, double maker48)
    {
        double stops;
        if (maker33 < 1.0)
            stops = maker48 <= 0.01 ? -20.0 * maker48 + 1.8 : -0.101 * maker48 + 1.601;
        else
            stops = maker48 <= 0.01 ? -70.0 * maker48 + 3.0 : -0.303 * maker48 + 2.303;
        return Math.Pow(2.0, Math.Max(stops, 0.0));
    }

    // ---- reading the headroom -------------------------------------------------------------------------

    private const int MaxMetadataBytes = 1 << 20;
    private const int MaxIfdEntries = 4096;

    /// <summary>
    /// The headroom for the auxiliary item <paramref name="aux"/> of <paramref name="image"/>: the image's Exif
    /// <c>MakerNote</c> first (Apple's route), then the aux item's XMP. Null when neither carries one; a non-finite value
    /// or one over <see cref="MaxHeadroom"/> is <see cref="InvalidDataException"/>.
    /// </summary>
    internal static (double Headroom, HeifAppleHeadroomSource Source)? FindHeadroom(HeifFile heif, ReadOnlySpan<byte> file, HeifItem image, HeifItem aux)
    {
        foreach (var meta in heif.MetadataOf(image))
        {
            if (meta.Type != "Exif" || !Readable(heif, file, meta)) continue;
            if (TryMakerNote(heif.ReadItemData(file, meta), out var maker33, out var maker48))
                return (Checked(HeadroomFromStops(maker33, maker48), "MakerNote keys 33 and 48"), HeifAppleHeadroomSource.MakerNote);
        }
        foreach (var meta in heif.MetadataOf(aux))
        {
            if (meta.Type != "mime" || !Readable(heif, file, meta)) continue;
            if (TryXmpCapacityMax(heif.ReadItemData(file, meta), out var log2))
                return (Checked(Math.Pow(2, log2), "XMP hdrgm:HDRCapacityMax"), HeifAppleHeadroomSource.Xmp);
        }
        return null;
    }

    private static bool Readable(HeifFile heif, ReadOnlySpan<byte> file, HeifItem item)
    {
        if (item.Protected || item.Location is null) return false;
        long total = 0;
        foreach (var (_, length) in heif.ItemRanges(file.Length, item)) total += length;
        return total <= MaxMetadataBytes;
    }

    private static double Checked(double headroom, string from)
    {
        if (!double.IsFinite(headroom) || headroom > MaxHeadroom)
            throw new InvalidDataException($"The headroom from the {from} is {headroom}; an HDR gain map's is finite and at most {MaxHeadroom}.");
        return headroom;
    }

    /// <summary>
    /// Keys 33 and 48 of Apple's <c>MakerNote</c> in an Exif item (23008-12 A.2.1: a 4-byte offset to the TIFF header, then
    /// the Exif). Walks IFD0 to the Exif IFD to tag 0x927C, whose value is <c>"Apple iOS\0"</c>, a 2-byte version, a byte-order
    /// mark (<c>MM</c> or <c>II</c>) and an IFD at offset 14 whose value offsets count from the start of the note.
    /// Anything that does not fit (no Exif, no note, another make, a missing key) is false, never an exception.
    /// </summary>
    internal static bool TryMakerNote(ReadOnlySpan<byte> exifItem, out double maker33, out double maker48)
    {
        maker33 = maker48 = 0;
        if (exifItem.Length < 12) return false;
        var skip = BinaryPrimitives.ReadUInt32BigEndian(exifItem);
        if (skip > (uint)(exifItem.Length - 12)) return false;
        var tiff = exifItem[(int)(4 + skip)..];

        if (!ByteOrder(tiff, out var little)) return false;
        if (Read16(tiff, 2, little) != 0x002A) return false;
        var ifd0 = Read32(tiff, 4, little);
        if (!FindEntry(tiff, ifd0, little, 0x8769, out var exifPointer) || exifPointer.Type != 4 || exifPointer.Count != 1) return false;
        if (!FindEntry(tiff, exifPointer.Value, little, 0x927C, out var note) || note.Type != 7) return false;
        // The value is in the entry when it fits four bytes; a note never does.
        if (note.Count < 16 || note.Value > (uint)tiff.Length || note.Count > (uint)tiff.Length - note.Value) return false;
        var data = tiff.Slice((int)note.Value, (int)note.Count);

        if (!data.StartsWith("Apple iOS\0"u8) || data.Length < 16) return false;
        if (!ByteOrder(data[12..], out var noteLittle)) return false;
        var found33 = false;
        var found48 = false;
        var count = Read16(data, 14, noteLittle);
        if (count > MaxIfdEntries) return false;
        for (var i = 0; i < count; i++)
        {
            var at = 16 + 12 * i;
            if (at + 12 > data.Length) break;
            var tag = Read16(data, at, noteLittle);
            if (tag is not (33 or 48)) continue;
            if (!TryNumber(data, at, noteLittle, out var value)) continue;
            if (tag == 33) { maker33 = value; found33 = true; } else { maker48 = value; found48 = true; }
        }
        return found33 && found48;
    }

    private readonly record struct Entry(ushort Type, uint Count, uint Value, int At);

    private static bool ByteOrder(ReadOnlySpan<byte> data, out bool little)
    {
        little = false;
        if (data.Length < 2) return false;
        if (data[0] == 'I' && data[1] == 'I') { little = true; return true; }
        return data[0] == 'M' && data[1] == 'M';
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static uint Read32(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    private static bool FindEntry(ReadOnlySpan<byte> data, uint ifd, bool little, ushort tag, out Entry entry)
    {
        entry = default;
        if (ifd > (uint)data.Length - 2) return false;
        var count = Read16(data, (int)ifd, little);
        if (count > MaxIfdEntries) return false;
        for (var i = 0; i < count; i++)
        {
            var at = (int)ifd + 2 + 12 * i;
            if (at + 12 > data.Length) return false;
            if (Read16(data, at, little) != tag) continue;
            entry = new Entry(Read16(data, at + 2, little), Read32(data, at + 4, little), Read32(data, at + 8, little), at);
            return true;
        }
        return false;
    }

    /// <summary>One number of an IFD entry at <paramref name="at"/> of <paramref name="note"/> (offsets from its start): RATIONAL, SRATIONAL, FLOAT or DOUBLE, count 1.</summary>
    private static bool TryNumber(ReadOnlySpan<byte> note, int at, bool little, out double value)
    {
        value = 0;
        var type = Read16(note, at + 2, little);
        var count = Read32(note, at + 4, little);
        var raw = Read32(note, at + 8, little);
        if (count != 1) return false;
        switch (type)
        {
            case 11: // FLOAT, in the entry itself
                value = BitConverter.Int32BitsToSingle((int)raw);
                return double.IsFinite(value);
            case 5 or 10 or 12: // 8 bytes, at the offset
                if (raw > (uint)note.Length - 8) return false;
                if (type == 12)
                {
                    var bits = little ? BinaryPrimitives.ReadInt64LittleEndian(note[(int)raw..]) : BinaryPrimitives.ReadInt64BigEndian(note[(int)raw..]);
                    value = BitConverter.Int64BitsToDouble(bits);
                    return double.IsFinite(value);
                }
                var numerator = Read32(note, (int)raw, little);
                var denominator = Read32(note, (int)raw + 4, little);
                if (denominator == 0) return false;
                value = type == 5 ? (double)numerator / denominator : (double)(int)numerator / (int)denominator;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// <c>hdrgm:HDRCapacityMax</c> (the log2 of the headroom) in an XMP packet, as an attribute or an element. A text
    /// search, not an XML parse: the packet is a few hundred bytes and the key's own name is all that is wanted.
    /// </summary>
    internal static bool TryXmpCapacityMax(ReadOnlySpan<byte> xmp, out double log2)
    {
        log2 = 0;
        var text = Encoding.UTF8.GetString(xmp);
        const string Key = "hdrgm:HDRCapacityMax";
        var at = text.IndexOf(Key, StringComparison.Ordinal);
        if (at < 0) return false;
        at += Key.Length;
        // attribute: ="1.5" (or '1.5'); element: >1.5<
        while (at < text.Length && text[at] is ' ' or '=' or '>' or '"' or '\'') at++;
        var end = at;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '.' or '-' or '+' or 'e' or 'E')) end++;
        return end > at && double.TryParse(text.AsSpan(at, end - at), NumberStyles.Float, CultureInfo.InvariantCulture, out log2) && double.IsFinite(log2);
    }
}
