using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Heif;

/// <summary>
/// Reads the structure of a HEIF file (ISO/IEC 23008-12 over the ISO/IEC 14496-12 box format): its
/// brands, items, item locations, references and properties. Nothing is decoded and nothing is sized
/// from what the file declares; <see cref="HeifFile.Describe"/> does that, against <see cref="HeifLimits"/>.
/// <para>
/// Malformed structure throws <see cref="InvalidDataException"/>. Structure that is well formed but
/// outside what this reader handles (an item stored in another file, an item built from other items'
/// bytes) throws <see cref="NotSupportedException"/> when that item's data is asked for, not when the
/// file is read, so one exotic metadata item does not make the image unreadable.
/// </para>
/// </summary>
public static class HeifReader
{
    // Brands that say "HEVC-coded HEIF" (23008-12 Annex B): heic/heix are the image brands, heim/heis
    // the layered and scalable ones. mif1 alone is NOT enough to claim a file, because AVIF lists it too.
    private static readonly string[] HevcBrands = ["heic", "heix", "heim", "heis"];

    /// <summary>
    /// True if <paramref name="header"/> starts with an <c>ftyp</c> box naming an HEVC-coded HEIF brand
    /// as its major brand or one of its compatible brands. Needs the whole <c>ftyp</c> box (typically
    /// 24-40 bytes); a header cut inside it answers false. Canon's CR3 (<c>crx </c>) and AVIF
    /// (<c>avif</c>) are both ISOBMFF too, and neither is claimed.
    /// </summary>
    public static bool IsHeif(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16) return false;
        var size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (type != FourCc.Ftyp || size < 16 || size > header.Length || (size - 16) % 4 != 0) return false;

        if (IsHevcBrand(header.Slice(8, 4))) return true;
        for (var offset = 16; offset + 4 <= size; offset += 4)
            if (IsHevcBrand(header.Slice(offset, 4))) return true;
        return false;
    }

    private static bool IsHevcBrand(ReadOnlySpan<byte> brand)
    {
        foreach (var candidate in HevcBrands)
            if (brand.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(candidate))) return true;
        return false;
    }

    /// <summary>Reads the item structure of the HEIF file in <paramref name="file"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not HEIF or its boxes are malformed.</exception>
    public static HeifFile Read(ReadOnlySpan<byte> file)
    {
        var top = IsoBoxes.Children(file, 0, file.Length);
        if (top.Count == 0 || top[0].Type != FourCc.Ftyp)
            throw new InvalidDataException("Not an ISOBMFF file: the first box is not 'ftyp'.");

        var (majorBrand, compatibleBrands) = ReadFtyp(file, top[0]);

        var metaBox = IsoBoxes.Find(top, FourCc.Meta)
            ?? throw new InvalidDataException("No 'meta' box at the top level, so there are no items.");
        IsoBoxes.FullBoxHeader(file, metaBox);
        var meta = IsoBoxes.Children(file, metaBox, skip: 4);

        var hdlr = Single(meta, FourCc.Hdlr, required: true)!.Value;
        var handler = ReadHandler(file, hdlr);
        if (handler != FourCc.Pict)
            throw new InvalidDataException($"The 'meta' handler is '{FourCc.ToString(handler)}', not 'pict': this is not an image collection.");

        var items = ReadItemInfo(file, Single(meta, FourCc.Iinf, required: true)!.Value);
        var byId = new Dictionary<uint, HeifItem>(items.Count);
        foreach (var item in items)
            if (!byId.TryAdd(item.Id, item))
                throw new InvalidDataException($"Item ID {item.Id} is declared twice.");

        var pitm = Single(meta, FourCc.Pitm, required: true)!.Value;
        var primaryId = ReadPrimaryItem(file, pitm);
        if (!byId.ContainsKey(primaryId))
            throw new InvalidDataException($"The primary item {primaryId} is not declared in 'iinf'.");

        if (Single(meta, FourCc.Iloc, required: false) is { } iloc)
            ReadItemLocations(file, iloc, byId);

        if (Single(meta, FourCc.Iref, required: false) is { } iref)
            ReadItemReferences(file, iref, byId);

        if (Single(meta, FourCc.Iprp, required: false) is { } iprp)
            ReadItemProperties(file, iprp, byId);

        var idat = Single(meta, FourCc.Idat, required: false);
        return new HeifFile(majorBrand, compatibleBrands, primaryId, items, byId,
            idat is { } d ? (d.BodyStart, d.BodyLength) : null);
    }

    private static IsoBox? Single(List<IsoBox> boxes, uint type, bool required)
    {
        IsoBox? found = null;
        foreach (var box in boxes)
        {
            if (box.Type != type) continue;
            if (found is not null)
                throw new InvalidDataException($"The 'meta' box holds more than one '{FourCc.ToString(type)}'.");
            found = box;
        }
        if (found is null && required)
            throw new InvalidDataException($"The 'meta' box has no '{FourCc.ToString(type)}'.");
        return found;
    }

    private static (string Major, IReadOnlyList<string> Compatible) ReadFtyp(ReadOnlySpan<byte> file, IsoBox ftyp)
    {
        var reader = new BigEndianReader(ftyp.Body(file), "ftyp");
        var major = FourCc.ToString(reader.U32());
        reader.U32(); // minor_version
        var compatible = new List<string>(reader.Remaining / 4);
        while (reader.Remaining >= 4) compatible.Add(FourCc.ToString(reader.U32()));
        return (major, compatible);
    }

    private static uint ReadHandler(ReadOnlySpan<byte> file, IsoBox hdlr)
    {
        var reader = new BigEndianReader(hdlr.Body(file), "hdlr");
        reader.U32(); // version, flags
        reader.U32(); // pre_defined
        return reader.U32();
    }

    private static uint ReadPrimaryItem(ReadOnlySpan<byte> file, IsoBox pitm)
    {
        var (version, _) = IsoBoxes.FullBoxHeader(file, pitm);
        var reader = new BigEndianReader(pitm.Body(file), "pitm");
        reader.Skip(4);
        return version == 0 ? reader.U16() : reader.U32();
    }

    // ---- iinf / infe (14496-12 8.11.6) ------------------------------------------------------------

    private static List<HeifItem> ReadItemInfo(ReadOnlySpan<byte> file, IsoBox iinf)
    {
        var (version, _) = IsoBoxes.FullBoxHeader(file, iinf);
        var countBytes = version == 0 ? 2 : 4;
        var reader = new BigEndianReader(iinf.Body(file), "iinf");
        reader.Skip(4);
        var declared = version == 0 ? reader.U16() : reader.U32();

        var items = new List<HeifItem>();
        foreach (var infe in IsoBoxes.Children(file, iinf, skip: 4 + countBytes))
        {
            if (infe.Type != FourCc.Infe) continue;
            items.Add(ReadItemInfoEntry(file, infe));
        }

        if (items.Count != declared)
            throw new InvalidDataException($"'iinf' declares {declared} items but holds {items.Count} 'infe' boxes.");
        return items;
    }

    private static HeifItem ReadItemInfoEntry(ReadOnlySpan<byte> file, IsoBox infe)
    {
        var (version, flags) = IsoBoxes.FullBoxHeader(file, infe);
        var reader = new BigEndianReader(infe.Body(file), "infe");
        reader.Skip(4);
        var hidden = (flags & 1) != 0;

        if (version < 2)
        {
            // Pre-HEIF entries: no item type, so never an image. Kept so their IDs still resolve.
            var legacyId = reader.U16();
            var legacyProtection = reader.U16();
            var legacyName = reader.NullTerminated();
            return new HeifItem(legacyId, "", legacyName, hidden, legacyProtection != 0, null);
        }

        var id = version == 2 ? reader.U16() : reader.U32();
        var protection = reader.U16();
        var type = reader.U32();
        var name = reader.Remaining > 0 ? reader.NullTerminated() : "";
        string? contentType = null;
        if (type == FourCc.Mime && reader.Remaining > 0)
            contentType = reader.NullTerminated();

        return new HeifItem(id, FourCc.ToString(type), name, hidden, protection != 0, contentType);
    }

    // ---- iloc (14496-12 8.11.3) -------------------------------------------------------------------

    private static void ReadItemLocations(ReadOnlySpan<byte> file, IsoBox iloc, Dictionary<uint, HeifItem> items)
    {
        var (version, _) = IsoBoxes.FullBoxHeader(file, iloc);
        if (version > 2)
            throw new InvalidDataException($"'iloc' version {version} is not defined.");

        var reader = new BigEndianReader(iloc.Body(file), "iloc");
        reader.Skip(4);
        var sizes = reader.U8();
        var offsetSize = sizes >> 4;
        var lengthSize = sizes & 0xF;
        var more = reader.U8();
        var baseOffsetSize = more >> 4;
        var indexSize = version >= 1 ? more & 0xF : 0;
        var itemCount = version < 2 ? reader.U16() : reader.U32();

        for (var i = 0u; i < itemCount; i++)
        {
            var id = version < 2 ? reader.U16() : reader.U32();
            var constructionMethod = version >= 1 ? reader.U16() & 0xF : 0;
            var dataReferenceIndex = reader.U16();
            var baseOffset = reader.Sized(baseOffsetSize);
            var extentCount = reader.U16();

            // Every extent must cost input, or a few bytes could declare 65535 of them per item. With all three
            // field widths 0 an extent is zero bytes wide and every one is the same (offset 0, "to the end"), so
            // only one is meaningful; otherwise the extents must fit in what is left of the box.
            var entrySize = (indexSize > 0 ? indexSize : 0) + offsetSize + lengthSize;
            if (entrySize == 0 ? extentCount > 1 : (long)extentCount * entrySize > reader.Remaining)
                throw new InvalidDataException($"'iloc' declares {extentCount} extents for item {id} of {entrySize} bytes each; the box has {reader.Remaining} left.");
            var extents = new List<(ulong, ulong)>(extentCount);
            for (var e = 0; e < extentCount; e++)
            {
                if (indexSize > 0) reader.Sized(indexSize);
                var offset = reader.Sized(offsetSize);
                var length = reader.Sized(lengthSize);
                extents.Add((offset, length));
            }

            if (!items.TryGetValue(id, out var item))
                throw new InvalidDataException($"'iloc' locates item {id}, which 'iinf' does not declare.");
            if (item.Location is not null)
                throw new InvalidDataException($"'iloc' locates item {id} twice.");
            item.Location = new HeifItemLocation(constructionMethod, dataReferenceIndex, baseOffset, extents);
        }
    }

    // ---- iref (14496-12 8.11.12) ------------------------------------------------------------------

    private static void ReadItemReferences(ReadOnlySpan<byte> file, IsoBox iref, Dictionary<uint, HeifItem> items)
    {
        var (version, _) = IsoBoxes.FullBoxHeader(file, iref);
        foreach (var box in IsoBoxes.Children(file, iref, skip: 4))
        {
            var reader = new BigEndianReader(box.Body(file), FourCc.ToString(box.Type));
            var from = version == 0 ? reader.U16() : reader.U32();
            var count = reader.U16();
            var to = new uint[Math.Min((int)count, reader.Remaining / (version == 0 ? 2 : 4))];
            if (to.Length != count)
                throw new InvalidDataException($"An '{FourCc.ToString(box.Type)}' reference from item {from} declares {count} targets but has room for {to.Length}.");
            for (var i = 0; i < to.Length; i++)
                to[i] = version == 0 ? reader.U16() : reader.U32();

            if (!items.TryGetValue(from, out var item))
                throw new InvalidDataException($"An '{FourCc.ToString(box.Type)}' reference comes from item {from}, which is not declared.");
            foreach (var target in to)
                if (!items.ContainsKey(target))
                    throw new InvalidDataException($"An '{FourCc.ToString(box.Type)}' reference from item {from} points at item {target}, which is not declared.");

            item._references.Add(new HeifItemReference(FourCc.ToString(box.Type), to));
        }
    }

    // ---- iprp / ipco / ipma (23008-12 9.3) --------------------------------------------------------

    private static void ReadItemProperties(ReadOnlySpan<byte> file, IsoBox iprp, Dictionary<uint, HeifItem> items)
    {
        var children = IsoBoxes.Children(file, iprp);
        var ipco = IsoBoxes.Find(children, FourCc.Ipco)
            ?? throw new InvalidDataException("'iprp' has no 'ipco' property container.");

        var properties = new List<HeifProperty>();
        foreach (var box in IsoBoxes.Children(file, ipco))
            properties.Add(HeifProperties.Read(file, box));

        foreach (var ipma in children)
        {
            if (ipma.Type != FourCc.Ipma) continue;
            var (version, flags) = IsoBoxes.FullBoxHeader(file, ipma);
            var reader = new BigEndianReader(ipma.Body(file), "ipma");
            reader.Skip(4);
            var entryCount = reader.U32();
            for (var i = 0u; i < entryCount; i++)
            {
                var id = version < 1 ? reader.U16() : reader.U32();
                if (!items.TryGetValue(id, out var item))
                    throw new InvalidDataException($"'ipma' associates properties with item {id}, which is not declared.");

                var associations = reader.U8();
                for (var a = 0; a < associations; a++)
                {
                    bool essential;
                    int index;
                    if ((flags & 1) != 0)
                    {
                        var word = reader.U16();
                        essential = (word & 0x8000) != 0;
                        index = word & 0x7FFF;
                    }
                    else
                    {
                        var b = reader.U8();
                        essential = (b & 0x80) != 0;
                        index = b & 0x7F;
                    }

                    if (index == 0) continue; // 0 means "no property"
                    if (index > properties.Count)
                        throw new InvalidDataException($"'ipma' gives item {id} property {index}, but 'ipco' holds {properties.Count}.");
                    item._properties.Add(new HeifPropertyAssociation(properties[index - 1], essential));
                }
            }
        }
    }
}

/// <summary>A HEIF file's item structure, as read by <see cref="HeifReader.Read"/>.</summary>
public sealed class HeifFile
{
    private readonly Dictionary<uint, HeifItem> _byId;
    private readonly (int Start, int Length)? _idat;

    internal HeifFile(string majorBrand, IReadOnlyList<string> compatibleBrands, uint primaryItemId,
        IReadOnlyList<HeifItem> items, Dictionary<uint, HeifItem> byId, (int, int)? idat)
    {
        MajorBrand = majorBrand;
        CompatibleBrands = compatibleBrands;
        PrimaryItemId = primaryItemId;
        Items = items;
        _byId = byId;
        _idat = idat;
    }

    public string MajorBrand { get; }

    public IReadOnlyList<string> CompatibleBrands { get; }

    public uint PrimaryItemId { get; }

    /// <summary>Every item, in <c>iinf</c> order.</summary>
    public IReadOnlyList<HeifItem> Items { get; }

    public HeifItem PrimaryItem => _byId[PrimaryItemId];

    public HeifItem? GetItem(uint id) => _byId.GetValueOrDefault(id);

    /// <summary>The image items that are thumbnails of <paramref name="image"/> (a <c>thmb</c> reference to it).</summary>
    public IReadOnlyList<HeifItem> ThumbnailsOf(HeifItem image) => ItemsReferencing(image, "thmb");

    /// <summary>The metadata items describing <paramref name="image"/> (a <c>cdsc</c> reference to it): Exif, XMP.</summary>
    public IReadOnlyList<HeifItem> MetadataOf(HeifItem image) => ItemsReferencing(image, "cdsc");

    private List<HeifItem> ItemsReferencing(HeifItem target, string type)
    {
        var found = new List<HeifItem>();
        foreach (var item in Items)
            if (item.ReferencesOfType(type).Contains(target.Id)) found.Add(item);
        return found;
    }

    /// <summary>
    /// The bytes of <paramref name="item"/>, its extents concatenated. <paramref name="file"/> must be the
    /// same bytes the file was read from.
    /// </summary>
    /// <exception cref="InvalidDataException">The item has no location, or an extent lies outside the file.</exception>
    /// <exception cref="NotSupportedException">The item is protected, lives in another file, or is built from other items.</exception>
    public byte[] ReadItemData(ReadOnlySpan<byte> file, HeifItem item)
    {
        var ranges = ItemRanges(file.Length, item);
        long total = 0;
        foreach (var (_, length) in ranges) total += length;
        // Extents may overlap (14496-12 does not forbid it), so their sum is not bounded by the file; an item's
        // data larger than the file holding it is a file repeating itself, refused before it is copied.
        if (total > file.Length)
            throw new InvalidDataException($"Item {item.Id}'s extents add up to {total} bytes, more than the {file.Length}-byte file holds.");

        var data = new byte[total];
        var at = 0;
        foreach (var (start, length) in ranges)
        {
            file.Slice(start, length).CopyTo(data.AsSpan(at));
            at += length;
        }
        return data;
    }

    /// <summary>The absolute file ranges holding <paramref name="item"/>'s bytes, checked to lie inside the file.</summary>
    internal List<(int Start, int Length)> ItemRanges(int fileLength, HeifItem item)
    {
        if (item.Protected)
            throw new NotSupportedException($"Item {item.Id} is protected (encrypted).");
        var location = item.Location
            ?? throw new InvalidDataException($"Item {item.Id} has no entry in 'iloc', so its data cannot be found.");
        if (location.DataReferenceIndex != 0)
            throw new NotSupportedException($"Item {item.Id}'s data is in another file (data reference {location.DataReferenceIndex}).");

        long regionStart, regionLength;
        switch (location.ConstructionMethod)
        {
            case 0: regionStart = 0; regionLength = fileLength; break;
            case 1:
                if (_idat is not { } idat)
                    throw new InvalidDataException($"Item {item.Id} is stored in 'idat', but the file has none.");
                (regionStart, regionLength) = (idat.Start, idat.Length);
                break;
            case 2: throw new NotSupportedException($"Item {item.Id} is constructed from other items' data (iloc construction method 2).");
            default: throw new InvalidDataException($"Item {item.Id} has iloc construction method {location.ConstructionMethod}, which is not defined.");
        }

        var ranges = new List<(int, int)>(location.Extents.Count);
        foreach (var (offset, length) in location.Extents)
        {
            // Unsigned 64-bit arithmetic that cannot wrap: each term is compared against the region
            // before it is added.
            if (location.BaseOffset > (ulong)regionLength || offset > (ulong)regionLength - location.BaseOffset)
                throw new InvalidDataException($"An extent of item {item.Id} starts past the end of its data.");
            var start = location.BaseOffset + offset;
            var available = (ulong)regionLength - start;
            // A length of 0 means "to the end of the data" (14496-12 8.11.3.3).
            var take = length == 0 ? available : length;
            if (take > available)
                throw new InvalidDataException($"An extent of item {item.Id} runs {take - available} bytes past the end of its data.");
            ranges.Add(((int)(regionStart + (long)start), (int)take));
        }
        return ranges;
    }

    /// <summary>
    /// <paramref name="image"/> as a reader would show it: size, coding, colour, orientation, with a grid's
    /// tiles resolved and checked. Charges the declared size against <see cref="HeifLimits"/>.
    /// </summary>
    /// <param name="file">The bytes the file was read from (a grid's layout is item data).</param>
    /// <param name="image">An <c>hvc1</c> or <c>grid</c> item.</param>
    /// <exception cref="InvalidDataException">The image is malformed, or larger than the limits allow.</exception>
    /// <exception cref="NotSupportedException">The image is of a type this reader does not handle.</exception>
    public HeifImage Describe(ReadOnlySpan<byte> file, HeifItem image)
    {
        HeifGrid? grid = null;
        HeifItem coded;
        uint width, height;

        switch (image.Type)
        {
            case "hvc1":
                coded = image;
                (width, height) = SizeOf(image);
                break;

            case "grid":
                grid = ReadGrid(file, image);
                width = grid.OutputWidth;
                height = grid.OutputHeight;
                coded = _byId[grid.TileItemIds[0]];
                break;

            default:
                throw new NotSupportedException($"Item {image.Id} is a '{image.Type}' item; only 'hvc1' and 'grid' images are described.");
        }

        if ((long)width * height > HeifLimits.MaxImagePixels)
            throw new InvalidDataException($"Item {image.Id} declares {width}x{height}, more than the {HeifLimits.MaxImagePixels}-pixel limit.");

        var coding = coded.Property<HevcDecoderConfiguration>()
            ?? throw new InvalidDataException($"HEVC item {coded.Id} has no 'hvcC' decoder configuration.");
        var (tileWidth, tileHeight) = SizeOf(coded);

        if (grid is not null)
            CheckTiles(image, grid, coding, tileWidth, tileHeight);

        // Colour and bit depth may sit on the derived image, on its tiles, or (Canon) both; the image's
        // own property wins, as 23008-12 has the derived image's properties describe its output.
        var pixi = image.Property<HeifPixelInformation>() ?? coded.Property<HeifPixelInformation>();
        var bitDepth = pixi is { BitsPerChannel.Count: > 0 } ? pixi.BitsPerChannel[0] : coding.BitDepthLuma;

        return new HeifImage
        {
            Item = image,
            Width = width,
            Height = height,
            Grid = grid,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            Coding = coding,
            BitDepth = bitDepth,
            Nclx = image.Property<HeifNclxColour>() ?? coded.Property<HeifNclxColour>(),
            Icc = image.Property<HeifIccColour>() ?? coded.Property<HeifIccColour>(),
            Rotation = image.Property<HeifRotation>()?.Degrees ?? 0,
            Mirror = image.Property<HeifMirror>()?.Axis,
            CleanAperture = image.Property<HeifCleanAperture>(),
        };
    }

    /// <summary>The primary item, described. See <see cref="Describe"/>.</summary>
    public HeifImage DescribePrimary(ReadOnlySpan<byte> file) => Describe(file, PrimaryItem);

    private static (uint Width, uint Height) SizeOf(HeifItem item)
    {
        var ispe = item.Property<HeifImageSize>()
            ?? throw new InvalidDataException($"Image item {item.Id} has no 'ispe' size, which every image item must carry.");
        if (ispe.Width == 0 || ispe.Height == 0)
            throw new InvalidDataException($"Image item {item.Id} declares an empty size {ispe.Width}x{ispe.Height}.");
        if (item.Type == "hvc1" && (ispe.Width > HeifLimits.MaxCodedDimension || ispe.Height > HeifLimits.MaxCodedDimension))
            throw new InvalidDataException($"HEVC item {item.Id} declares {ispe.Width}x{ispe.Height}; no HEVC level allows a side over {HeifLimits.MaxCodedDimension}.");
        return (ispe.Width, ispe.Height);
    }

    private HeifGrid ReadGrid(ReadOnlySpan<byte> file, HeifItem item)
    {
        // The layout is a few bytes of item data; read it in place rather than copying.
        var ranges = ItemRanges(file.Length, item);
        Span<byte> layout = stackalloc byte[12];
        var have = 0;
        foreach (var (start, length) in ranges)
        {
            var take = Math.Min(length, layout.Length - have);
            file.Slice(start, take).CopyTo(layout[have..]);
            have += take;
            if (have == layout.Length) break;
        }

        var reader = new BigEndianReader(layout[..have], "grid");
        var version = reader.U8();
        if (version != 0)
            throw new NotSupportedException($"Grid item {item.Id} has layout version {version}; only 0 is defined.");
        var flags = reader.U8();
        var rows = reader.U8() + 1;
        var columns = reader.U8() + 1;
        var wide = (flags & 1) != 0;
        var outputWidth = wide ? reader.U32() : reader.U16();
        var outputHeight = wide ? reader.U32() : reader.U16();

        var tiles = item.ReferencesOfType("dimg");
        if (tiles.Count != rows * columns)
            throw new InvalidDataException($"Grid item {item.Id} is {rows}x{columns} but references {tiles.Count} tiles.");
        if (outputWidth == 0 || outputHeight == 0)
            throw new InvalidDataException($"Grid item {item.Id} declares an empty output {outputWidth}x{outputHeight}.");

        // The grid's own ispe, when present, must agree with the layout: two sizes that disagree leave no
        // way to know which the writer meant.
        if (item.Property<HeifImageSize>() is { } ispe && (ispe.Width != outputWidth || ispe.Height != outputHeight))
            throw new InvalidDataException($"Grid item {item.Id} lays out {outputWidth}x{outputHeight} but its 'ispe' says {ispe.Width}x{ispe.Height}.");

        return new HeifGrid(rows, columns, outputWidth, outputHeight, tiles);
    }

    private void CheckTiles(HeifItem image, HeifGrid grid, HevcDecoderConfiguration coding, uint tileWidth, uint tileHeight)
    {
        foreach (var id in grid.TileItemIds)
        {
            var tile = _byId[id];
            if (tile.Type != "hvc1")
                throw new NotSupportedException($"Grid item {image.Id}'s tile {id} is a '{tile.Type}' item; only 'hvc1' tiles are handled.");
            var (w, h) = SizeOf(tile);
            if (w != tileWidth || h != tileHeight)
                throw new InvalidDataException($"Grid item {image.Id}'s tiles differ in size ({tileWidth}x{tileHeight} and {w}x{h}); 23008-12 requires them equal.");
            var c = tile.Property<HevcDecoderConfiguration>()
                ?? throw new InvalidDataException($"Tile {id} has no 'hvcC' decoder configuration.");
            if (c.ChromaFormat != coding.ChromaFormat || c.BitDepthLuma != coding.BitDepthLuma || c.BitDepthChroma != coding.BitDepthChroma)
                throw new InvalidDataException($"Grid item {image.Id}'s tiles are coded differently (tile {id} disagrees with tile {grid.TileItemIds[0]}).");
        }

        if ((ulong)grid.Columns * tileWidth < grid.OutputWidth || (ulong)grid.Rows * tileHeight < grid.OutputHeight)
            throw new InvalidDataException(
                $"Grid item {image.Id}'s {grid.Rows}x{grid.Columns} tiles of {tileWidth}x{tileHeight} do not cover its {grid.OutputWidth}x{grid.OutputHeight} output.");
    }
}

/// <summary>Parsing of the individual <c>ipco</c> property boxes.</summary>
internal static class HeifProperties
{
    public static HeifProperty Read(ReadOnlySpan<byte> file, IsoBox box)
    {
        var body = box.Body(file);
        switch (box.Type)
        {
            case FourCc.Ispe:
            {
                var r = new BigEndianReader(body, "ispe");
                r.Skip(4);
                return new HeifImageSize(r.U32(), r.U32());
            }

            case FourCc.Colr:
            {
                var r = new BigEndianReader(body, "colr");
                var colourType = r.U32();
                switch (colourType)
                {
                    case FourCc.Nclx:
                        var primaries = r.U16();
                        var transfer = r.U16();
                        var matrix = r.U16();
                        var fullRange = (r.U8() & 0x80) != 0;
                        return new HeifNclxColour(
                            (ColorPrimaries)Math.Min(primaries, (ushort)255),
                            (TransferFunction)Math.Min(transfer, (ushort)255),
                            (MatrixCoefficients)Math.Min(matrix, (ushort)255),
                            fullRange);
                    case FourCc.Prof:
                    case FourCc.RICC:
                        return new HeifIccColour(r.Bytes(r.Remaining).ToArray(), colourType == FourCc.RICC);
                    default:
                        return new HeifUnknownProperty("colr", box.BodyLength);
                }
            }

            case FourCc.Pixi:
            {
                var r = new BigEndianReader(body, "pixi");
                r.Skip(4);
                var channels = r.U8();
                return new HeifPixelInformation(r.Bytes(channels).ToArray());
            }

            case FourCc.Irot:
            {
                var r = new BigEndianReader(body, "irot");
                return new HeifRotation((r.U8() & 3) * 90);
            }

            case FourCc.Imir:
            {
                var r = new BigEndianReader(body, "imir");
                return new HeifMirror((HeifMirrorAxis)(r.U8() & 1));
            }

            case FourCc.Clap:
            {
                var r = new BigEndianReader(body, "clap");
                return new HeifCleanAperture(r.U32(), r.U32(), r.U32(), r.U32(), r.S32(), r.U32(), r.S32(), r.U32());
            }

            case FourCc.HvcC:
                return ReadHevcConfiguration(body);

            case FourCc.AuxC:
            {
                // 23008-12 6.5.8: version/flags, a null-terminated URN, then type-specific subtype bytes.
                // A URN with no terminator is malformed: kept as an unknown property rather than guessed at.
                var r = new BigEndianReader(body, "auxC");
                r.Skip(4);
                var rest = r.Bytes(r.Remaining);
                var end = rest.IndexOf((byte)0);
                if (end < 0) return new HeifUnknownProperty("auxC", box.BodyLength);
                return new HeifAuxiliaryType(System.Text.Encoding.UTF8.GetString(rest[..end]), rest[(end + 1)..].ToArray());
            }

            default:
                return new HeifUnknownProperty(FourCc.ToString(box.Type), box.BodyLength);
        }
    }

    /// <summary>The HEVCDecoderConfigurationRecord (14496-15 8.3.3.1.2).</summary>
    public static HevcDecoderConfiguration ReadHevcConfiguration(ReadOnlySpan<byte> body)
    {
        var r = new BigEndianReader(body, "hvcC");
        var version = r.U8();
        if (version != 1)
            throw new NotSupportedException($"'hvcC' configuration version {version}; only 1 is defined.");

        var profile = r.U8();
        var compatibility = r.U32();
        var constraints = (ulong)r.U32() << 16 | r.U16();
        var level = r.U8();
        r.U16(); // reserved '1111' + min_spatial_segmentation_idc
        r.U8();  // reserved '111111' + parallelismType
        var chroma = r.U8() & 3;
        var lumaDepth = (r.U8() & 7) + 8;
        var chromaDepth = (r.U8() & 7) + 8;
        r.U16(); // avgFrameRate
        var lengthSize = (r.U8() & 3) + 1;
        if (lengthSize == 3)
            throw new InvalidDataException("'hvcC' declares 3-byte NAL unit lengths; only 1, 2 and 4 are allowed.");

        var arrayCount = r.U8();
        var arrays = new List<HevcNalUnitArray>(arrayCount);
        for (var a = 0; a < arrayCount; a++)
        {
            var head = r.U8();
            var count = r.U16();
            var nals = new List<byte[]>(Math.Min((int)count, r.Remaining / 2));
            for (var n = 0; n < count; n++)
            {
                var length = r.U16();
                nals.Add(r.Bytes(length).ToArray());
            }
            arrays.Add(new HevcNalUnitArray(head & 0x3F, (head & 0x80) != 0, nals));
        }

        return new HevcDecoderConfiguration
        {
            ProfileSpace = profile >> 6,
            HighTier = (profile & 0x20) != 0,
            ProfileIdc = profile & 0x1F,
            ProfileCompatibilityFlags = compatibility,
            ConstraintIndicatorFlags = constraints,
            LevelIdc = level,
            ChromaFormat = (HevcChromaFormat)chroma,
            BitDepthLuma = lumaDepth,
            BitDepthChroma = chromaDepth,
            LengthSize = lengthSize,
            NalUnitArrays = arrays,
        };
    }
}
