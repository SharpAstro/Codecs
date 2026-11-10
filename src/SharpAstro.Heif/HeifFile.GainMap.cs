using SharpAstro.GainMap;
using SharpAstro.Jpeg;

namespace SharpAstro.Heif;

/// <summary>Which item of a gain-map file is the primary (the one a reader that knows nothing of gain maps shows).</summary>
public enum HeifGainMapLayout
{
    /// <summary>
    /// The primary item is the base image and the <c>tmap</c> item sits beside it (hidden, or visible and in an
    /// <c>altr</c> group with the base): Samsung's files. A reader that ignores the <c>tmap</c> shows the SDR base.
    /// </summary>
    BaseIsPrimary,

    /// <summary>
    /// The primary item is the <c>tmap</c> and the base image is its first input, hidden or in an <c>altr</c> group
    /// with it (23008-12's own arrangement, what libheif writes): a reader that ignores the <c>tmap</c> has no image.
    /// </summary>
    TmapIsPrimary,

    /// <summary>The <c>tmap</c> is not the primary and neither is its base (a file with several images, one of which has a gain map).</summary>
    Other,
}

/// <summary>
/// A gain map found in a HEIF file by <see cref="HeifFile.GainMapOf"/>, in one of two carriers (<see cref="Dialect"/>).
/// <para>
/// <see cref="HeifGainMapDialect.IsoTmap"/>: the <c>tmap</c> derived image item (23008-12, the tone-map derived image), the
/// two items its <c>dimg</c> references name, in order (the base image, then the gain map), and the ISO/TS 21496-1 metadata
/// that is the <c>tmap</c> item's own data.
/// </para>
/// <para>
/// <see cref="HeifGainMapDialect.AppleAux"/>: no <c>tmap</c>; the base is the image asked about, the map is its Apple
/// gain-map auxiliary image, and <see cref="Metadata"/> is a one-channel summary of the Apple headroom (gain range 1 to the
/// headroom, no offsets), kept so a caller reading only the metadata sees the authored range. The details are in <see cref="Apple"/>.
/// </para>
/// </summary>
/// <param name="Tmap">The <c>tmap</c> item; null for <see cref="HeifGainMapDialect.AppleAux"/>.</param>
/// <param name="Base">The base image: the first <c>dimg</c> input, an <c>hvc1</c> or <c>grid</c> item.</param>
/// <param name="Map">The gain-map image: the second <c>dimg</c> input (often 4:0:0), or Apple's auxiliary item; an <c>hvc1</c> or <c>grid</c> item.</param>
/// <param name="Metadata">The parsed metadata: per-channel parameters, the headrooms, the direction.</param>
/// <param name="Layout">Which of the three arrangements the file uses.</param>
/// <param name="Alternatives">The <c>altr</c> group holding both the <c>tmap</c> and the base, or null when there is none.</param>
/// <param name="Dialect">Which carrier the map was found in. A file with both uses the <c>tmap</c>.</param>
/// <param name="Apple">The Apple-specific part, non-null exactly for <see cref="HeifGainMapDialect.AppleAux"/>.</param>
public sealed record HeifGainMapInfo(
    HeifItem? Tmap, HeifItem Base, HeifItem Map, GainMapMetadata Metadata, HeifGainMapLayout Layout, HeifEntityGroup? Alternatives,
    HeifGainMapDialect Dialect = HeifGainMapDialect.IsoTmap, HeifAppleGainMap? Apple = null);

public sealed partial class HeifFile
{
    /// <summary>
    /// The most bytes a <c>tmap</c> item's data may hold: the metadata is a few dozen bytes to under 200 (one or
    /// three channels of five fractions), so 4 KiB leaves room for appended fields of a newer writer and no more.
    /// </summary>
    internal const int MaxTmapDataBytes = 4096;

    /// <summary>
    /// The gain map that goes with <paramref name="image"/>, or null when it has none. <paramref name="image"/> is
    /// either the <c>tmap</c> item itself or the image the <c>tmap</c> derives from (a <c>tmap</c> whose first
    /// <c>dimg</c> input it is), so both layouts of a gain-map file are found from the primary item:
    /// <list type="bullet">
    /// <item><description><b>The primary is the base</b> and the <c>tmap</c> is hidden or beside it, alone or in an
    /// <c>altr</c> group with it (Samsung): found by scanning for a <c>tmap</c> whose first input is the primary.</description></item>
    /// <item><description><b>The primary is the <c>tmap</c></b>, its first input the base (hidden, or in the
    /// <c>altr</c> group): found because the primary is itself a <c>tmap</c>.</description></item>
    /// </list>
    /// <para>
    /// <b>Apple's older files</b> carry the map only as an <c>auxl</c> auxiliary image of the item, typed by its
    /// <c>auxC</c> property (<see cref="HeifAuxiliaryType"/>) as <see cref="HeifAppleGainMap.AuxiliaryUrn"/>, with no
    /// <c>tmap</c>. When no <c>tmap</c> is found for <paramref name="image"/>, that is looked for next
    /// (<see cref="HeifGainMapInfo.Dialect"/> says which was found; a file with both gives the <c>tmap</c>). The headroom
    /// is read from the image's Exif <c>MakerNote</c> or the auxiliary item's XMP (see <see cref="HeifAppleGainMap"/>, which
    /// also says how far to trust it). Other auxiliary images (alpha <c>urn:mpeg:hevc:2015:auxid:1</c>, depth <c>:2</c>,
    /// Samsung's or anything else) are not gain maps and are ignored. An Apple map whose headroom is missing, or is 1 or
    /// less (no HDR effect), is no gain map here: null, and the item is its SDR picture; a headroom that is not finite or
    /// over <see cref="HeifAppleGainMap.MaxHeadroom"/> is <see cref="InvalidDataException"/>.
    /// </para>
    /// <para>
    /// The structure is validated: exactly two <c>dimg</c> inputs, different, neither a <c>tmap</c>, both
    /// <c>hvc1</c> or <c>grid</c> items, and metadata that parses (see <see cref="Iso21496Metadata"/>). The
    /// <c>tmap</c> item's data is a version byte (0, the only one defined) and then the ISO/TS 21496-1 payload, as
    /// the Samsung file in the tests has it.
    /// </para>
    /// </summary>
    /// <param name="file">The bytes the file was read from (the metadata is item data).</param>
    /// <exception cref="InvalidDataException">The <c>tmap</c> does not have two distinct image inputs, or its data is malformed.</exception>
    /// <exception cref="NotSupportedException">An input is not an <c>hvc1</c> or <c>grid</c> item, or the <c>tmap</c> has a version this reader does not know.</exception>
    public HeifGainMapInfo? GainMapOf(ReadOnlySpan<byte> file, HeifItem image)
    {
        ArgumentNullException.ThrowIfNull(image);
        HeifItem? tmap = null;
        if (image.Type == "tmap")
        {
            tmap = image;
        }
        else
        {
            foreach (var item in Items)
            {
                if (item.Type != "tmap") continue;
                var firstInput = item.ReferencesOfType("dimg");
                if (firstInput.Count > 0 && firstInput[0] == image.Id) { tmap = item; break; }
            }
        }
        if (tmap is null) return AppleGainMapOf(file, image);

        var inputs = tmap.ReferencesOfType("dimg");
        if (inputs.Count != 2)
            throw new InvalidDataException($"The 'tmap' item {tmap.Id} derives from {inputs.Count} images; it must name exactly two (the base, then the gain map).");
        if (inputs[0] == inputs[1])
            throw new InvalidDataException($"The 'tmap' item {tmap.Id} names item {inputs[0]} as both its base and its gain map.");
        var (baseItem, mapItem) = (_byId[inputs[0]], _byId[inputs[1]]);
        foreach (var input in new[] { baseItem, mapItem })
        {
            if (input.Type == "tmap")
                throw new InvalidDataException($"The 'tmap' item {tmap.Id} takes the 'tmap' item {input.Id} as an input.");
            if (input.Type is not ("hvc1" or "grid"))
                throw new NotSupportedException($"The 'tmap' item {tmap.Id}'s input {input.Id} is a '{input.Type}' item; only 'hvc1' and 'grid' images are handled.");
        }

        var ranges = ItemRanges(file.Length, tmap);
        long total = 0;
        foreach (var (_, length) in ranges) total += length;
        if (total > MaxTmapDataBytes)
            throw new InvalidDataException($"The 'tmap' item {tmap.Id} holds {total} bytes of metadata; no ISO/TS 21496-1 structure is over {MaxTmapDataBytes}.");
        var data = ReadItemData(file, tmap);
        if (data.Length == 0)
            throw new InvalidDataException($"The 'tmap' item {tmap.Id} has no data.");
        if (data[0] != 0)
            throw new NotSupportedException($"The 'tmap' item {tmap.Id} has version {data[0]}; only 0 is defined.");
        var metadata = Iso21496Metadata.Parse(data.AsSpan(1));

        var layout = tmap.Id == PrimaryItemId ? HeifGainMapLayout.TmapIsPrimary
            : baseItem.Id == PrimaryItemId ? HeifGainMapLayout.BaseIsPrimary
            : HeifGainMapLayout.Other;
        HeifEntityGroup? alternatives = null;
        foreach (var group in EntityGroups)
        {
            if (group.Type == "altr" && group.EntityIds.Contains(tmap.Id) && group.EntityIds.Contains(baseItem.Id))
            {
                alternatives = group;
                break;
            }
        }
        return new HeifGainMapInfo(tmap, baseItem, mapItem, metadata, layout, alternatives);
    }

    private HeifGainMapInfo? AppleGainMapOf(ReadOnlySpan<byte> file, HeifItem image)
    {
        foreach (var aux in Items)
        {
            if (aux.Property<HeifAuxiliaryType>() is not { Urn: HeifAppleGainMap.AuxiliaryUrn }) continue;
            if (!aux.ReferencesOfType("auxl").Contains(image.Id)) continue;
            if (aux.Type is not ("hvc1" or "grid"))
                throw new NotSupportedException($"The Apple gain map {aux.Id} is a '{aux.Type}' item; only 'hvc1' and 'grid' images are handled.");
            if (aux.Id == image.Id) continue;

            if (HeifAppleGainMap.FindHeadroom(this, file, image, aux) is not { } found) return null;
            var (headroom, source) = found;
            if (!(headroom > 1)) return null;

            var metadata = new GainMapMetadata
            {
                GainMapMin = 1,
                GainMapMax = headroom,
                Gamma = 1,
                OffsetSdr = 0,
                OffsetHdr = 0,
                HdrCapacityMin = 1,
                HdrCapacityMax = headroom,
            };
            var layout = image.Id == PrimaryItemId ? HeifGainMapLayout.BaseIsPrimary : HeifGainMapLayout.Other;
            return new HeifGainMapInfo(null, image, aux, metadata, layout, null, HeifGainMapDialect.AppleAux,
                new HeifAppleGainMap(aux, headroom, source));
        }
        return null;
    }

    /// <summary>The gain map of the primary item, or null. See <see cref="GainMapOf"/>.</summary>
    public HeifGainMapInfo? GainMapOfPrimary(ReadOnlySpan<byte> file) => GainMapOf(file, PrimaryItem);
}
