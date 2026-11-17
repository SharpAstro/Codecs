using System.IO.Compression;
using System.Xml;

namespace SharpAstro.Svg;

/// <summary>
/// Reads an SVG document into a resolved <see cref="SharpAstro.VectorGraphics.VectorScene"/>. It does not draw: there is no
/// rasteriser and no font code here, and the caller renders the scene with whatever it has.
/// </summary>
/// <remarks>
/// <para>Written from the W3C SVG 1.1 and SVG 2 specifications. Read so far: the root's size, <c>viewBox</c> and
/// <c>preserveAspectRatio</c>, and nested <c>svg</c>; <c>g</c> and <c>a</c>; <c>defs</c>, <c>use</c> and <c>symbol</c>;
/// <c>path</c>, <c>rect</c>, <c>circle</c>, <c>ellipse</c>, <c>line</c>, <c>polyline</c>, <c>polygon</c>;
/// <c>transform</c>; solid, gradient and pattern fills and strokes with every stroke property; opacity; <c>display</c> and
/// <c>visibility</c>; <c>clipPath</c>, <c>mask</c> and the clip of a nested viewport; markers and <c>paint-order</c>;
/// <c>image</c> (the picture is carried as the file it is, not decoded, and one in another file is named, not fetched);
/// presentation attributes, the <c>style</c> attribute and <c>style</c> sheets, cascaded and inherited
/// as CSS says. Everything else is left out of the scene and listed in <see cref="SvgDocument.Issues"/>.</para>
/// <para>The XML is read with DTD processing ignored and no resolver, so nothing outside the document is fetched and
/// entities are not expanded; a document that uses an entity it declares itself is refused. A gzip stream (SVGZ) is
/// undone first.</para>
/// </remarks>
public static class SvgReader
{
    internal const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XLinkNamespace = "http://www.w3.org/1999/xlink";

    /// <summary>Reads a document from its bytes (plain or gzip).</summary>
    /// <exception cref="InvalidDataException">Not well-formed XML, not an SVG document, or over a limit in <paramref name="options"/>.</exception>
    public static SvgDocument Read(byte[] data, SvgReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= SvgReadOptions.Default;
        // The array is all there, so it can be looked at and handed over as a stream that knows its length: the XML
        // reader then sizes its buffers to the document, which for an icon is most of what a read allocates.
        using var stream = new MemoryStream(data, writable: false);
        if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using XmlReader zipped = XmlReader.Create(gzip, Settings(options));
            return Read(zipped, options);
        }
        using XmlReader xml = XmlReader.Create(stream, Settings(options));
        return Read(xml, options);
    }

    /// <summary>Reads a document from a stream (plain or gzip), from its current position. The stream is left open.</summary>
    /// <exception cref="InvalidDataException">Not well-formed XML, not an SVG document, or over a limit in <paramref name="options"/>.</exception>
    public static SvgDocument Read(Stream stream, SvgReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= SvgReadOptions.Default;

        // Two bytes decide plain or gzip; they are handed back in front of the rest, so the stream need not seek.
        byte[] head = new byte[2];
        int got = stream.ReadAtLeast(head, 2, throwOnEndOfStream: false);
        using var whole = new PrefixedStream(head.AsMemory(0, got), stream);
        if (got == 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            using var gzip = new GZipStream(whole, CompressionMode.Decompress, leaveOpen: true);
            using XmlReader zipped = XmlReader.Create(gzip, Settings(options));
            return Read(zipped, options);
        }
        using XmlReader xml = XmlReader.Create(whole, Settings(options));
        return Read(xml, options);
    }

    /// <summary>Reads a document from its text.</summary>
    /// <exception cref="InvalidDataException">Not well-formed XML, not an SVG document, or over a limit in <paramref name="options"/>.</exception>
    public static SvgDocument Parse(string svg, SvgReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(svg);
        options ??= SvgReadOptions.Default;
        using var text = new StringReader(svg);
        using XmlReader xml = XmlReader.Create(text, Settings(options));
        return Read(xml, options);
    }

    private static XmlReaderSettings Settings(SvgReadOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxElements, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPathVerbs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSelectorTests, 1);
        return new XmlReaderSettings
        {
            // Ignore, not Prohibit: a DOCTYPE is common in SVG 1.1 files and harmless when it is skipped.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = options.MaxCharacters,
            CloseInput = false,
        };
    }

    private static SvgDocument Read(XmlReader xml, SvgReadOptions options)
    {
        SvgTree tree;
        try
        {
            tree = ReadTree(xml, options);
        }
        catch (XmlException e)
        {
            throw new InvalidDataException($"The SVG is not XML this reader accepts: {e.Message}", e);
        }
        return new SvgSceneBuilder(options, tree).Build();
    }

    /// <summary>
    /// The SVG elements of the document as a tree. Elements of other namespaces, and everything inside them, are not
    /// kept; attributes are kept when they have no namespace, and <c>xlink:href</c> as <c>href</c> when there is no
    /// plain one (SVG 2 lets the plain one win). The text of <c>style</c> elements is kept; no other text is.
    /// </summary>
    private static SvgTree ReadTree(XmlReader xml, SvgReadOptions options)
    {
        var ids = new Dictionary<string, SvgElement>(StringComparer.Ordinal);
        var sheets = new List<SvgElement>();
        SvgElement? root = null;
        var open = new Stack<SvgElement?>();
        int elements = 0;
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.EndElement)
            {
                open.Pop();
                continue;
            }
            if (xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
            {
                if (open.Count > 0 && open.Peek() is { Name: "style" } style) style.Text += xml.Value;
                continue;
            }
            if (xml.NodeType != XmlNodeType.Element) continue;

            if (++elements > options.MaxElements)
            {
                throw new InvalidDataException($"The SVG has more than {options.MaxElements} elements.");
            }
            if (open.Count >= options.MaxDepth)
            {
                throw new InvalidDataException($"The SVG nests elements more than {options.MaxDepth} deep.");
            }

            bool svg = xml.NamespaceURI == SvgNamespace;
            SvgElement? element = null;
            if (root is null)
            {
                if (!svg || xml.LocalName != "svg")
                {
                    throw new InvalidDataException(
                        $"Not an SVG document: the root element is <{xml.Name}>, not <svg> in the namespace {SvgNamespace}.");
                }
                root = element = new SvgElement("svg");
            }
            else if (svg && open.Peek() is { } parent)
            {
                element = new SvgElement(xml.LocalName);
                parent.Add(element);
            }

            if (element is not null && xml.HasAttributes)
            {
                string? xlinkHref = null;
                while (xml.MoveToNextAttribute())
                {
                    if (xml.NamespaceURI.Length == 0) element.Set(xml.LocalName, xml.Value);
                    else if (xml.NamespaceURI == XLinkNamespace && xml.LocalName == "href") xlinkHref = xml.Value;
                }
                xml.MoveToElement();
                if (xlinkHref is not null && element.Get("href") is null) element.Set("href", xlinkHref);
                // The first element with an id keeps it, as in a browser's getElementById.
                if (element.Get("id") is { Length: > 0 } id) ids.TryAdd(id, element);
            }
            if (element is { Name: "style" }) sheets.Add(element);
            if (!xml.IsEmptyElement) open.Push(element);
        }
        if (root is null) throw new InvalidDataException("Not an SVG document: it has no root element.");
        return new SvgTree(root, ids, sheets);
    }

    /// <summary>A few bytes already taken from a stream, then the rest of it. Read-only and forward-only.</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefix.IsEmpty) return rest.Read(buffer);
            int n = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..n].CopyTo(buffer);
            _prefix = _prefix[n..];
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>The document as read: the root, the elements by <c>id</c>, and the <c>style</c> elements in document order.</summary>
internal sealed record SvgTree(SvgElement Root, Dictionary<string, SvgElement> Ids, List<SvgElement> StyleSheets);

/// <summary>Things worked out once for an element that few elements have.</summary>
internal sealed class SvgElementExtras
{
    /// <summary>For a <c>path</c> that has been drawn with markers: the verb count of its path after each path command.</summary>
    public int[]? CommandEnds;

    /// <summary>For an <c>image</c> with a data URI, once decoded: its bytes (empty when it could not be read) and media type. Every instance shares them.</summary>
    public ReadOnlyMemory<byte> Picture;
    public string? MediaType;
    public bool PictureRead;
}

/// <summary>An SVG element as read: its local name, its un-namespaced attributes and its SVG children.</summary>
internal sealed class SvgElement(string name)
{
    // A few pairs searched in order, not a dictionary: an element has two or three attributes, and a dictionary an
    // element was a quarter of what reading a large document allocated.
    private KeyValuePair<string, string>[]? _attributes;
    private int _attributeCount;
    private List<SvgElement>? _children;

    public string Name { get; } = name;

    public SvgElement? Parent { get; private set; }

    /// <summary>The place among the parent's children.</summary>
    public int Index { get; private set; }

    /// <summary>The character data of a <c>style</c> element.</summary>
    public string? Text { get; set; }

    /// <summary>The path of a <c>path</c>, <c>polyline</c> or <c>polygon</c>, once it has been drawn: every instance shares it.</summary>
    public SharpAstro.VectorGraphics.VectorPath? Path { get; set; }

    /// <summary>
    /// What only a few elements ever need, kept behind one reference so that the many that do not pay eight bytes for
    /// it and no more: a document is mostly elements.
    /// </summary>
    public SvgElementExtras? Extras { get; set; }

    /// <summary>The declarations that apply to this element, once worked out against the style sheets.</summary>
    public SvgDeclaration[]? Cascade { get; set; }

    public IReadOnlyList<SvgElement> Children => (IReadOnlyList<SvgElement>?)_children ?? [];

    public ReadOnlySpan<KeyValuePair<string, string>> Attributes => _attributes.AsSpan(0, _attributeCount);

    /// <summary>The attribute's text, or null when the element does not have it.</summary>
    public string? Get(string attribute)
    {
        for (int i = 0; i < _attributeCount; i++)
        {
            if (_attributes![i].Key == attribute) return _attributes[i].Value;
        }
        return null;
    }

    public void Set(string attribute, string value)
    {
        for (int i = 0; i < _attributeCount; i++)
        {
            if (_attributes![i].Key == attribute)
            {
                _attributes[i] = new KeyValuePair<string, string>(attribute, value);
                return;
            }
        }
        if (_attributes is null || _attributeCount == _attributes.Length)
        {
            Array.Resize(ref _attributes, _attributeCount == 0 ? 4 : _attributeCount * 2);
        }
        _attributes[_attributeCount++] = new KeyValuePair<string, string>(attribute, value);
    }

    public void Add(SvgElement child)
    {
        child.Parent = this;
        child.Index = (_children ??= []).Count;
        _children.Add(child);
    }
}
