using System.IO.Compression;
using System.Xml;

namespace SharpAstro.Svg;

/// <summary>
/// Reads an SVG document into a resolved <see cref="SharpAstro.Vector.VectorScene"/>. It does not draw: there is no
/// rasteriser and no font code here, and the caller renders the scene with whatever it has.
/// </summary>
/// <remarks>
/// <para>Written from the W3C SVG 1.1 and SVG 2 specifications. Read so far: the root's size, <c>viewBox</c> and
/// <c>preserveAspectRatio</c>; <c>g</c> and <c>a</c>; <c>path</c>, <c>rect</c>, <c>circle</c>, <c>ellipse</c>,
/// <c>line</c>, <c>polyline</c>, <c>polygon</c>; <c>transform</c>; solid fills and strokes with every stroke property;
/// opacity; <c>display</c> and <c>visibility</c>; presentation attributes and the <c>style</c> attribute, inherited as CSS
/// says. Everything else is left out of the scene and listed in <see cref="SvgDocument.Issues"/>.</para>
/// <para>The XML is read with DTD processing ignored and no resolver, so nothing outside the document is fetched and
/// entities are not expanded; a document that uses an entity it declares itself is refused. A gzip stream (SVGZ) is
/// undone first.</para>
/// </remarks>
public static class SvgReader
{
    internal const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>Reads a document from its bytes (plain or gzip).</summary>
    /// <exception cref="InvalidDataException">Not well-formed XML, not an SVG document, or over a limit in <paramref name="options"/>.</exception>
    public static SvgDocument Read(byte[] data, SvgReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream(data, writable: false);
        return Read(stream, options);
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
        SvgElement root;
        try
        {
            root = ReadTree(xml, options);
        }
        catch (XmlException e)
        {
            throw new InvalidDataException($"The SVG is not XML this reader accepts: {e.Message}", e);
        }
        return new SvgSceneBuilder(options).Build(root);
    }

    /// <summary>
    /// The SVG elements of the document as a tree. Elements of other namespaces, and everything inside them, are not
    /// kept; attributes are kept when they have no namespace.
    /// </summary>
    private static SvgElement ReadTree(XmlReader xml, SvgReadOptions options)
    {
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
                while (xml.MoveToNextAttribute())
                {
                    if (xml.NamespaceURI.Length == 0) element.Set(xml.LocalName, xml.Value);
                }
                xml.MoveToElement();
            }
            if (!xml.IsEmptyElement) open.Push(element);
        }
        return root ?? throw new InvalidDataException("Not an SVG document: it has no root element.");
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

/// <summary>An SVG element as read: its local name, its un-namespaced attributes and its SVG children.</summary>
internal sealed class SvgElement(string name)
{
    private Dictionary<string, string>? _attributes;
    private List<SvgElement>? _children;

    public string Name { get; } = name;

    public IReadOnlyList<SvgElement> Children => (IReadOnlyList<SvgElement>?)_children ?? [];

    public IEnumerable<KeyValuePair<string, string>> Attributes => (IEnumerable<KeyValuePair<string, string>>?)_attributes ?? [];

    /// <summary>The attribute's text, or null when the element does not have it.</summary>
    public string? Get(string attribute) => _attributes is not null && _attributes.TryGetValue(attribute, out string? value) ? value : null;

    public void Set(string attribute, string value) => (_attributes ??= new Dictionary<string, string>(StringComparer.Ordinal))[attribute] = value;

    public void Add(SvgElement child) => (_children ??= []).Add(child);
}
