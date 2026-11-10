using System.Buffers.Binary;
using System.Text;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Writes HEIF files box by box (ISO/IEC 23008-12 over 14496-12) for the container tests: items with
/// their data in <c>mdat</c> or <c>idat</c>, references, and properties given as raw box bodies, with
/// the versions and field widths of <c>iloc</c>, <c>iref</c> and <c>ipma</c> selectable, so the reader's
/// handling of each variant is exercised by a real file rather than by calling its internals.
/// <para>
/// It also wraps a bare HEVC picture (an <c>hvcC</c> plus length-prefixed NAL units, as Canon's CR3
/// previews hold them) into a one-item HEIF, which is how Magick.NET (libheif) is made to decode those
/// previews as an oracle.
/// </para>
/// </summary>
internal sealed class HeifBuilder
{
    public sealed record Item(uint Id, string Type, byte[] Data, bool Hidden = false, bool InIdat = false, string? ContentType = null);

    private readonly List<Item> _items = [];
    private readonly List<(string Type, uint From, uint[] To)> _references = [];
    private readonly List<(string Type, byte[] Body)> _properties = [];
    private readonly List<(uint Item, int Property, bool Essential)> _associations = [];
    private readonly List<(string Type, uint GroupId, uint[] Entities)> _groups = [];

    public string MajorBrand { get; set; } = "heic";
    public string[] CompatibleBrands { get; set; } = ["mif1", "heic"];
    public uint PrimaryItemId { get; set; } = 1;
    public string Handler { get; set; } = "pict";
    public int IlocVersion { get; set; } = 1;
    public int IrefVersion { get; set; }
    public bool IpmaWideIndices { get; set; }
    public int IpmaVersion { get; set; }
    /// <summary>When set, writes this many items into iinf's count instead of the real number.</summary>
    public uint? DeclaredItemCount { get; set; }

    public HeifBuilder AddItem(Item item) { _items.Add(item); return this; }

    public HeifBuilder Reference(string type, uint from, params uint[] to) { _references.Add((type, from, to)); return this; }

    /// <summary>Adds an entity group (<c>grpl</c> child, 14496-12 8.15.3) such as <c>altr</c>.</summary>
    public HeifBuilder EntityGroup(string type, uint groupId, params uint[] entities) { _groups.Add((type, groupId, entities)); return this; }

    /// <summary>Adds a property box to <c>ipco</c> and returns its 1-based index.</summary>
    public int Property(string type, byte[] body) { _properties.Add((type, body)); return _properties.Count; }

    public HeifBuilder Associate(uint item, int property, bool essential = false) { _associations.Add((item, property, essential)); return this; }

    public HeifBuilder Associate(uint item, string type, byte[] body, bool essential = false) =>
        Associate(item, Property(type, body), essential);

    // ---- property bodies ---------------------------------------------------------------------------

    public static byte[] Ispe(uint width, uint height) => Concat(FullBoxHeader(0, 0), U32(width), U32(height));

    public static byte[] Nclx(int primaries, int transfer, int matrix, bool fullRange) =>
        Concat(Ascii("nclx"), U16((ushort)primaries), U16((ushort)transfer), U16((ushort)matrix), [(byte)(fullRange ? 0x80 : 0)]);

    public static byte[] Icc(byte[] profile, bool restricted = false) => Concat(Ascii(restricted ? "rICC" : "prof"), profile);

    public static byte[] Pixi(params byte[] bits) => Concat(FullBoxHeader(0, 0), [(byte)bits.Length], bits);

    public static byte[] Irot(int degrees) => [(byte)(degrees / 90 & 3)];

    public static byte[] Imir(int axis) => [(byte)axis];

    /// <summary>A <c>clap</c> body: the aperture's width and height as whole pixels, centred on the image plus the given offsets.</summary>
    public static byte[] Clap(uint width, uint height, int horizontalOffset = 0, int verticalOffset = 0) =>
        Concat(U32(width), U32(1), U32(height), U32(1), U32((uint)horizontalOffset), U32(1), U32((uint)verticalOffset), U32(1));

    /// <summary>
    /// An hvcC body with the given summary and NAL unit arrays. The profile and level default to what a
    /// 10-bit range-extensions still declares; the reader never decodes from these alone.
    /// </summary>
    public static byte[] HvcC(int chromaFormat = 1, int bitDepthLuma = 8, int bitDepthChroma = 8, int lengthSize = 4,
        int profileIdc = 1, int levelIdc = 93, params (int Type, byte[][] Nals)[] arrays)
    {
        var body = new List<byte>
        {
            1, (byte)profileIdc, 0x60, 0, 0, 0, // version, profile, compatibility flags
            0x90, 0, 0, 0, 0, 0,                // constraint indicator flags
            (byte)levelIdc,
            0xF0, 0x00,                         // min_spatial_segmentation_idc
            0xFC,                               // parallelismType
            (byte)(0xFC | chromaFormat),
            (byte)(0xF8 | (bitDepthLuma - 8)),
            (byte)(0xF8 | (bitDepthChroma - 8)),
            0, 0,                               // avgFrameRate
            (byte)(0x0C | (lengthSize - 1)),
            (byte)arrays.Length,
        };
        foreach (var (type, nals) in arrays)
        {
            body.Add((byte)(0x80 | type));
            body.AddRange(U16((ushort)nals.Length));
            foreach (var nal in nals)
            {
                body.AddRange(U16((ushort)nal.Length));
                body.AddRange(nal);
            }
        }
        return [.. body];
    }

    /// <summary>A grid item's payload (23008-12 6.6.2.3.2).</summary>
    public static byte[] GridLayout(int rows, int columns, uint outputWidth, uint outputHeight, bool wide = false) =>
        wide
            ? Concat([0, 1, (byte)(rows - 1), (byte)(columns - 1)], U32(outputWidth), U32(outputHeight))
            : Concat([0, 0, (byte)(rows - 1), (byte)(columns - 1)], U16((ushort)outputWidth), U16((ushort)outputHeight));

    // ---- file --------------------------------------------------------------------------------------

    public byte[] Build()
    {
        var ftyp = Box("ftyp", Concat(Ascii(MajorBrand), U32(0), Concat(CompatibleBrands.Select(Ascii).ToArray())));

        // Item data goes into mdat (or idat) in item order. iloc needs mdat's absolute offset, which
        // depends on the size of meta, which contains iloc: so lay meta out once with placeholder offsets
        // (their width is fixed), then again with the real ones.
        var idat = Concat(_items.Where(i => i.InIdat).Select(i => i.Data).ToArray());
        var metaLength = Meta(0, idat).Length;
        var mdatStart = ftyp.Length + metaLength + 8;
        var meta = Meta(mdatStart, idat);
        if (meta.Length != metaLength) throw new InvalidOperationException("meta changed size between passes");

        var mdat = Box("mdat", Concat(_items.Where(i => !i.InIdat).Select(i => i.Data).ToArray()));
        return Concat(ftyp, meta, mdat);
    }

    private byte[] Meta(long mdatStart, byte[] idat)
    {
        var hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Ascii(Handler), new byte[12], [0]));
        var pitm = FullBox("pitm", 0, 0, U16((ushort)PrimaryItemId));

        var infes = _items.Select(item => FullBox("infe", 2, item.Hidden ? 1u : 0u, Concat(
            U16((ushort)item.Id), U16(0), Ascii(item.Type), [0],
            item.ContentType is null ? [] : Concat(Encoding.ASCII.GetBytes(item.ContentType), [0])))).ToArray();
        var iinf = FullBox("iinf", 0, 0, Concat(U16((ushort)(DeclaredItemCount ?? (uint)_items.Count)), Concat(infes)));

        var boxes = new List<byte[]> { hdlr, pitm, iinf, Iloc(mdatStart) };
        if (_references.Count > 0) boxes.Add(Iref());
        if (_properties.Count > 0) boxes.Add(Iprp());
        if (_groups.Count > 0)
            boxes.Add(Box("grpl", Concat(_groups.Select(g => FullBox(g.Type, 0, 0, Concat(
                U32(g.GroupId), U32((uint)g.Entities.Length), Concat(g.Entities.Select(U32).ToArray())))).ToArray())));
        if (idat.Length > 0) boxes.Add(Box("idat", idat));
        return FullBox("meta", 0, 0, Concat(boxes.ToArray()));
    }

    /// <summary>Write each item's position as an 8-byte base_offset with extents relative to it.</summary>
    public bool IlocBaseOffsets { get; set; }

    /// <summary>Split every item of two or more bytes into two extents.</summary>
    public bool IlocTwoExtents { get; set; }

    private byte[] Iloc(long mdatStart)
    {
        // offset_size 4, length_size 4; base_offset_size 8 or 0; index_size 0.
        var body = new List<byte> { 0x44, (byte)(IlocBaseOffsets ? 0x80 : 0x00) };
        body.AddRange(IlocVersion < 2 ? U16((ushort)_items.Count) : U32((uint)_items.Count));
        long mdatAt = mdatStart, idatAt = 0;
        foreach (var item in _items)
        {
            body.AddRange(IlocVersion < 2 ? U16((ushort)item.Id) : U32(item.Id));
            if (IlocVersion >= 1) body.AddRange(U16((ushort)(item.InIdat ? 1 : 0)));
            else if (item.InIdat) throw new InvalidOperationException("iloc version 0 cannot point into idat");
            body.AddRange(U16(0)); // data_reference_index

            var at = item.InIdat ? idatAt : mdatAt;
            var origin = 0L;
            if (IlocBaseOffsets)
            {
                body.AddRange(U32((uint)(at >> 32)));
                body.AddRange(U32((uint)at));
                origin = at;
            }

            var split = IlocTwoExtents && item.Data.Length >= 2 ? item.Data.Length / 2 : item.Data.Length;
            var extents = split == item.Data.Length ? new[] { (at, item.Data.Length) } : new[] { (at, split), (at + split, item.Data.Length - split) };
            body.AddRange(U16((ushort)extents.Length));
            foreach (var (offset, length) in extents)
            {
                body.AddRange(U32((uint)(offset - origin)));
                body.AddRange(U32((uint)length));
            }

            if (item.InIdat) idatAt += item.Data.Length; else mdatAt += item.Data.Length;
        }
        return FullBox("iloc", (byte)IlocVersion, 0, [.. body]);
    }

    private byte[] Iref()
    {
        var wide = IrefVersion != 0;
        var references = _references.Select(r => Box(r.Type, Concat(
            wide ? U32(r.From) : U16((ushort)r.From),
            U16((ushort)r.To.Length),
            Concat(r.To.Select(t => wide ? U32(t) : U16((ushort)t)).ToArray())))).ToArray();
        return FullBox("iref", (byte)IrefVersion, 0, Concat(references));
    }

    private byte[] Iprp()
    {
        var ipco = Box("ipco", Concat(_properties.Select(p => Box(p.Type, p.Body)).ToArray()));
        var body = new List<byte>();
        var byItem = _associations.GroupBy(a => a.Item).ToList();
        body.AddRange(U32((uint)byItem.Count));
        foreach (var group in byItem)
        {
            body.AddRange(IpmaVersion < 1 ? U16((ushort)group.Key) : U32(group.Key));
            body.Add((byte)group.Count());
            foreach (var (_, property, essential) in group)
            {
                if (IpmaWideIndices) body.AddRange(U16((ushort)((essential ? 0x8000 : 0) | property)));
                else body.Add((byte)((essential ? 0x80 : 0) | property));
            }
        }
        var ipma = FullBox("ipma", (byte)IpmaVersion, IpmaWideIndices ? 1u : 0u, [.. body]);
        return Box("iprp", Concat(ipco, ipma));
    }

    // ---- primitives --------------------------------------------------------------------------------

    public static byte[] Box(string type, byte[] body) => Concat(U32((uint)(8 + body.Length)), Ascii(type), body);

    public static byte[] FullBox(string type, byte version, uint flags, byte[] body) => Box(type, Concat(FullBoxHeader(version, flags), body));

    public static byte[] FullBoxHeader(byte version, uint flags) => U32((uint)version << 24 | flags);

    public static byte[] U16(ushort value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); return b; }

    public static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    public static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts) { part.CopyTo(all, at); at += part.Length; }
        return all;
    }

    /// <summary>
    /// A one-item HEIF holding a single HEVC picture: <paramref name="hvcC"/> (a raw hvcC body),
    /// <paramref name="nalUnits"/> (length-prefixed as the hvcC says), its displayed size, and the
    /// colour and pixel-information boxes copied as they are.
    /// </summary>
    public static byte[] WrapHevcPicture(byte[] hvcC, byte[] nalUnits, uint width, uint height, byte[]? colr, byte[]? pixi)
    {
        var builder = new HeifBuilder().AddItem(new Item(1, "hvc1", nalUnits));
        builder.Associate(1, "hvcC", hvcC, essential: true);
        builder.Associate(1, "ispe", Ispe(width, height));
        if (colr is not null) builder.Associate(1, "colr", colr);
        if (pixi is not null) builder.Associate(1, "pixi", pixi);
        return builder.Build();
    }
}
