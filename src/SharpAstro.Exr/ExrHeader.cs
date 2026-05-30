namespace SharpAstro.Exr;

/// <summary>
/// The OpenEXR file header: the set of named, typed attributes that precede the
/// pixel data. This codec reads/writes the eight attributes required of every
/// scanline image (T.832-equivalent "required attributes" in the OpenEXR spec) and
/// skips any others on read. Attribute order is not significant to a conformant
/// reader; we emit a fixed canonical order.
/// </summary>
internal sealed class ExrHeader
{
    public List<ExrChannel> Channels = [];
    public ExrCompression Compression;
    public ExrLineOrder LineOrder = ExrLineOrder.IncreasingY;

    public int DataXMin, DataYMin, DataXMax, DataYMax;
    public int DispXMin, DispYMin, DispXMax, DispYMax;

    public float PixelAspectRatio = 1f;
    public float ScreenWindowCenterX = 0f;
    public float ScreenWindowCenterY = 0f;
    public float ScreenWindowWidth = 1f;

    public int Width => DataXMax - DataXMin + 1;
    public int Height => DataYMax - DataYMin + 1;

    /// <summary>OpenEXR sets the version "long names" flag when any name exceeds 31 chars.
    /// All our fixed attribute names/types are short, so only channel names can trip it.</summary>
    public bool RequiresLongNames
    {
        get
        {
            foreach (var c in Channels) if (c.Name.Length > 31) return true;
            return false;
        }
    }

    /// <summary>Channels in OpenEXR's canonical case-sensitive ascending name order.</summary>
    public List<ExrChannel> SortedChannels()
    {
        var list = new List<ExrChannel>(Channels);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    // ------------------------------------------------------------------ write

    public void WriteAttributes(ExrWriter w)
    {
        WriteChannels(w);
        WriteAttr(w, "compression", "compression", t => t.WriteByte((byte)Compression));
        WriteAttr(w, "dataWindow", "box2i", t => { t.WriteInt32(DataXMin); t.WriteInt32(DataYMin); t.WriteInt32(DataXMax); t.WriteInt32(DataYMax); });
        WriteAttr(w, "displayWindow", "box2i", t => { t.WriteInt32(DispXMin); t.WriteInt32(DispYMin); t.WriteInt32(DispXMax); t.WriteInt32(DispYMax); });
        WriteAttr(w, "lineOrder", "lineOrder", t => t.WriteByte((byte)LineOrder));
        WriteAttr(w, "pixelAspectRatio", "float", t => t.WriteSingle(PixelAspectRatio));
        WriteAttr(w, "screenWindowCenter", "v2f", t => { t.WriteSingle(ScreenWindowCenterX); t.WriteSingle(ScreenWindowCenterY); });
        WriteAttr(w, "screenWindowWidth", "float", t => t.WriteSingle(ScreenWindowWidth));
        w.WriteByte(0); // empty attribute name terminates the header
    }

    private void WriteChannels(ExrWriter w)
    {
        var value = new ExrWriter(64);
        foreach (var c in SortedChannels())
        {
            value.WriteNulString(c.Name);
            value.WriteInt32((int)c.Type);
            value.WriteByte(c.PLinear ? (byte)1 : (byte)0);
            value.WriteByte(0); value.WriteByte(0); value.WriteByte(0); // reserved
            value.WriteInt32(c.XSampling);
            value.WriteInt32(c.YSampling);
        }
        value.WriteByte(0); // channel-list terminator
        WriteAttrBytes(w, "channels", "chlist", value.ToArray());
    }

    private static void WriteAttr(ExrWriter w, string name, string type, Action<ExrWriter> writeValue)
    {
        var v = new ExrWriter(32);
        writeValue(v);
        WriteAttrBytes(w, name, type, v.ToArray());
    }

    private static void WriteAttrBytes(ExrWriter w, string name, string type, byte[] value)
    {
        w.WriteNulString(name);
        w.WriteNulString(type);
        w.WriteInt32(value.Length);
        w.WriteBytes(value);
    }

    // ------------------------------------------------------------------- read

    public static ExrHeader ReadAttributes(ref ExrReader r)
    {
        var h = new ExrHeader();
        bool sawData = false;
        while (true)
        {
            string name = r.ReadNulString();
            if (name.Length == 0) break; // end of header
            string type = r.ReadNulString();
            int size = r.ReadInt32();
            int end = r.Pos + size;

            switch (name)
            {
                case "channels":
                    h.Channels = ReadChannels(ref r);
                    break;
                case "compression":
                    h.Compression = (ExrCompression)r.ReadByte();
                    break;
                case "dataWindow":
                    h.DataXMin = r.ReadInt32(); h.DataYMin = r.ReadInt32(); h.DataXMax = r.ReadInt32(); h.DataYMax = r.ReadInt32();
                    sawData = true;
                    break;
                case "displayWindow":
                    h.DispXMin = r.ReadInt32(); h.DispYMin = r.ReadInt32(); h.DispXMax = r.ReadInt32(); h.DispYMax = r.ReadInt32();
                    break;
                case "lineOrder":
                    h.LineOrder = (ExrLineOrder)r.ReadByte();
                    break;
                case "pixelAspectRatio":
                    h.PixelAspectRatio = r.ReadSingle();
                    break;
                case "screenWindowCenter":
                    h.ScreenWindowCenterX = r.ReadSingle(); h.ScreenWindowCenterY = r.ReadSingle();
                    break;
                case "screenWindowWidth":
                    h.ScreenWindowWidth = r.ReadSingle();
                    break;
                // Unknown / optional attributes (chromaticities, owner, comments, ...) are skipped.
            }
            r.Seek(end); // robust against partially-consumed or skipped attributes
        }

        if (!sawData) throw new InvalidDataException("EXR header missing required dataWindow attribute.");
        if (h.Channels.Count == 0) throw new InvalidDataException("EXR header missing required channels attribute.");
        return h;
    }

    private static List<ExrChannel> ReadChannels(ref ExrReader r)
    {
        var channels = new List<ExrChannel>();
        while (true)
        {
            string name = r.ReadNulString();
            if (name.Length == 0) break; // channel-list terminator
            var type = (ExrPixelType)r.ReadInt32();
            byte pLinear = r.ReadByte();
            r.ReadByte(); r.ReadByte(); r.ReadByte(); // reserved
            int xs = r.ReadInt32();
            int ys = r.ReadInt32();
            channels.Add(new ExrChannel { Name = name, Type = type, PLinear = pLinear != 0, XSampling = xs, YSampling = ys });
        }
        return channels;
    }
}
