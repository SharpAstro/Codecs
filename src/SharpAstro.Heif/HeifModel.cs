using SharpAstro.Codecs.Abstractions;

namespace SharpAstro.Heif;

/// <summary>
/// One item of a HEIF file's <c>meta</c> box (ISO/IEC 23008-12 6.4, from the 14496-12 <c>infe</c>,
/// <c>iloc</c>, <c>iref</c> and <c>ipma</c> boxes): what it is, where its bytes are, which properties
/// describe it and which other items it points at.
/// </summary>
public sealed class HeifItem
{
    internal HeifItem(uint id, string type, string name, bool hidden, bool protectedItem, string? contentType)
    {
        Id = id;
        Type = type;
        Name = name;
        Hidden = hidden;
        Protected = protectedItem;
        ContentType = contentType;
    }

    /// <summary>The item ID other boxes refer to it by.</summary>
    public uint Id { get; }

    /// <summary>
    /// The four-character item type: <c>hvc1</c> for an HEVC-coded image, <c>grid</c> for a derived image
    /// tiled from others, <c>Exif</c>, <c>mime</c> (XMP among others), or empty for an item described by a
    /// pre-HEIF version 0/1 <c>infe</c> box, which carries no type.
    /// </summary>
    public string Type { get; }

    public string Name { get; }

    /// <summary>The <c>infe</c> "hidden" flag: the item is not meant to be shown on its own (grid tiles usually are).</summary>
    public bool Hidden { get; }

    /// <summary>The item is protected (encrypted, 14496-12 <c>ipro</c>); its data cannot be read as is.</summary>
    public bool Protected { get; }

    /// <summary>The MIME type of a <c>mime</c> item (<c>application/rdf+xml</c> for XMP), otherwise null.</summary>
    public string? ContentType { get; }

    /// <summary>The properties associated with this item, in <c>ipma</c> order.</summary>
    public IReadOnlyList<HeifPropertyAssociation> Properties => _properties;

    /// <summary>The item references from this item, in file order (<c>dimg</c> order is a grid's tile order).</summary>
    public IReadOnlyList<HeifItemReference> References => _references;

    internal readonly List<HeifPropertyAssociation> _properties = [];
    internal readonly List<HeifItemReference> _references = [];
    internal HeifItemLocation? Location { get; set; }

    /// <summary>The first property of type <typeparamref name="T"/> associated with this item, or null.</summary>
    public T? Property<T>() where T : HeifProperty
    {
        foreach (var association in _properties)
            if (association.Property is T match) return match;
        return null;
    }

    /// <summary>The item IDs this item references with <paramref name="type"/> (e.g. <c>dimg</c>), in order.</summary>
    public IReadOnlyList<uint> ReferencesOfType(string type)
    {
        List<uint>? ids = null;
        foreach (var reference in _references)
        {
            if (reference.Type != type) continue;
            ids ??= [];
            ids.AddRange(reference.ToItemIds);
        }
        return ids ?? (IReadOnlyList<uint>)Array.Empty<uint>();
    }

    public override string ToString() => $"item {Id} '{Type}'";
}

/// <summary>
/// One entity group of the <c>grpl</c> box (14496-12 8.15.3): its grouping type (<c>altr</c>: the entities are
/// alternatives of each other, a reader shows the first it supports), its ID and the entity (item) IDs in order.
/// </summary>
public sealed record HeifEntityGroup(string Type, uint GroupId, IReadOnlyList<uint> EntityIds);

/// <summary>A property associated with an item, and whether a reader must understand it to show the item.</summary>
public readonly record struct HeifPropertyAssociation(HeifProperty Property, bool Essential);

/// <summary>One reference of a given type from an item to others (<c>dimg</c>, <c>thmb</c>, <c>cdsc</c>, <c>auxl</c>...).</summary>
public sealed record HeifItemReference(string Type, IReadOnlyList<uint> ToItemIds);

/// <summary>Where an item's bytes are (14496-12 8.11.3, <c>iloc</c>).</summary>
internal sealed record HeifItemLocation(int ConstructionMethod, int DataReferenceIndex, ulong BaseOffset, IReadOnlyList<(ulong Offset, ulong Length)> Extents);

/// <summary>An item property from the <c>ipco</c> box. Unknown types are kept as <see cref="HeifUnknownProperty"/>.</summary>
public abstract record HeifProperty(string Type);

/// <summary><c>ispe</c>: the image's width and height in pixels, as coded (before any rotation, mirror or crop).</summary>
public sealed record HeifImageSize(uint Width, uint Height) : HeifProperty("ispe");

/// <summary>
/// <c>colr</c> of type <c>nclx</c>: colour signalled as ITU-T H.273 code points. The values are kept as the
/// file states them, including ones the enums have no name for.
/// </summary>
public sealed record HeifNclxColour(ColorPrimaries Primaries, TransferFunction Transfer, MatrixCoefficients Matrix, bool FullRange)
    : HeifProperty("colr");

/// <summary><c>colr</c> of type <c>prof</c> (unrestricted) or <c>rICC</c> (restricted): an ICC profile.</summary>
public sealed record HeifIccColour(byte[] Profile, bool Restricted) : HeifProperty("colr");

/// <summary><c>pixi</c>: bits per channel of the reconstructed image.</summary>
public sealed record HeifPixelInformation(IReadOnlyList<byte> BitsPerChannel) : HeifProperty("pixi");

/// <summary><c>irot</c>: rotate the image ANTICLOCKWISE by <see cref="Degrees"/> (0, 90, 180 or 270) for display.</summary>
public sealed record HeifRotation(int Degrees) : HeifProperty("irot");

/// <summary>
/// <c>imir</c>: mirror the image for display (23008-12 6.5.12). The axis value 0 (<see cref="HeifMirrorAxis.Vertical"/>)
/// swaps top and bottom and 1 (<see cref="HeifMirrorAxis.Horizontal"/>) swaps left and right: what libheif does,
/// measured, and so what files are written for. The names follow the field's two values, not a geometric claim.
/// </summary>
public sealed record HeifMirror(HeifMirrorAxis Axis) : HeifProperty("imir");

public enum HeifMirrorAxis
{
    /// <summary>Axis value 0: top and bottom swap (a vertical flip).</summary>
    Vertical = 0,

    /// <summary>Axis value 1: left and right swap (a horizontal flip).</summary>
    Horizontal = 1,
}

/// <summary>
/// <c>clap</c>: the clean aperture (14496-12 12.1.4), a crop centred on the image centre plus the stated
/// offsets. Every value is a fraction, numerator over denominator, as the box stores it.
/// </summary>
public sealed record HeifCleanAperture(
    uint WidthN, uint WidthD, uint HeightN, uint HeightD,
    int HorizontalOffsetN, uint HorizontalOffsetD, int VerticalOffsetN, uint VerticalOffsetD)
    : HeifProperty("clap");

/// <summary>
/// <c>auxC</c>: what an auxiliary image item (one with an <c>auxl</c> reference to its master) is, by URN:
/// <c>urn:mpeg:hevc:2015:auxid:1</c> alpha, <c>:2</c> depth, Apple's <c>urn:com:apple:photo:2020:aux:hdrgainmap</c>,
/// Samsung's <c>urn:com:samsung:photo:2024:aux:hdrgainmap</c>. The subtype bytes are the URN's own and kept raw.
/// </summary>
public sealed record HeifAuxiliaryType(string Urn, byte[] Subtype) : HeifProperty("auxC");

/// <summary>A property this reader does not interpret, kept so its presence (and essential flag) stays visible.</summary>
public sealed record HeifUnknownProperty(string BoxType, int Length) : HeifProperty(BoxType);

/// <summary>
/// The payload of a <c>grid</c> derived image item (23008-12 6.6.2.3): a <see cref="Rows"/> x
/// <see cref="Columns"/> arrangement of the input images named, row by row, by the grid's <c>dimg</c>
/// references, cropped on the right and bottom to <see cref="OutputWidth"/> x <see cref="OutputHeight"/>.
/// </summary>
public sealed record HeifGrid(int Rows, int Columns, uint OutputWidth, uint OutputHeight, IReadOnlyList<uint> TileItemIds);

/// <summary>Chroma format as HEVC numbers it (<c>chroma_format_idc</c>, H.265 Table 6-1).</summary>
public enum HevcChromaFormat
{
    Monochrome = 0,
    Yuv420 = 1,
    Yuv422 = 2,
    Yuv444 = 3,
}

/// <summary>
/// An image item as a reader would show it: its size, its coding, its colour and how to orient it, with a
/// grid's tiles resolved. Built by <see cref="HeifFile.Describe"/>, which checks the structure is
/// consistent (every tile present, coded alike and covering the output) and that the declared size is
/// within <see cref="HeifLimits"/>, before anyone allocates for it.
/// </summary>
public sealed record HeifImage
{
    /// <summary>The image item itself (<c>hvc1</c> or <c>grid</c>).</summary>
    public required HeifItem Item { get; init; }

    /// <summary>Width of the reconstructed image, before rotation, mirroring or clean-aperture crop.</summary>
    public required uint Width { get; init; }

    public required uint Height { get; init; }

    /// <summary>The grid, or null for a single coded image.</summary>
    public HeifGrid? Grid { get; init; }

    /// <summary>The coded size of each tile (for a single image, its own size).</summary>
    public required uint TileWidth { get; init; }

    public required uint TileHeight { get; init; }

    /// <summary>The HEVC decoder configuration of the coded image or tiles.</summary>
    public required HevcDecoderConfiguration Coding { get; init; }

    /// <summary>Bits per luma sample, from <c>pixi</c> when present, otherwise from <see cref="Coding"/>.</summary>
    public required int BitDepth { get; init; }

    public HevcChromaFormat ChromaFormat => Coding.ChromaFormat;

    /// <summary>The <c>nclx</c> colour, from the image item or else its first tile; null when none is signalled.</summary>
    public HeifNclxColour? Nclx { get; init; }

    /// <summary>The ICC profile, from the image item or else its first tile; null when none is present.</summary>
    public HeifIccColour? Icc { get; init; }

    /// <summary>Anticlockwise display rotation in degrees.</summary>
    public int Rotation { get; init; }

    public HeifMirrorAxis? Mirror { get; init; }

    public HeifCleanAperture? CleanAperture { get; init; }
}
