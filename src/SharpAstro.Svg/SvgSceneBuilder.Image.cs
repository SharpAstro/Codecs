using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

// The image element: a raster picture placed in the drawing. It is carried, not decoded.
internal sealed partial class SvgSceneBuilder
{
    private void Image(SvgElement element, in SvgStyle style, SvgAffine matrix, in SvgViewport viewport, List<VectorNode> output, int depth)
    {
        if (!style.Visible) return;
        if (element.Get("href")?.Trim() is not { Length: > 0 } href) return;

        double fontSize = style.FontSize;
        if (element.Get("width") is not { } widthText || element.Get("height") is not { } heightText
            || widthText.AsSpan().Trim() is "auto" || heightText.AsSpan().Trim() is "auto")
        {
            // Then the picture's own size decides, and that is only known once it is decoded, which is not done here.
            Issue(SvgIssueKind.Unsupported, "image with no width or height (sized by the picture)");
            return;
        }
        double x = Length(element, "x", viewport.Width, fontSize), y = Length(element, "y", viewport.Height, fontSize);
        double w = Length(element, "width", viewport.Width, fontSize), h = Length(element, "height", viewport.Height, fontSize);
        if (w < 0 || h < 0) Issue(SvgIssueKind.Invalid, "negative image size");
        if (w <= 0 || h <= 0) return;

        bool uniform = true, slice = false;
        double alignX = 0.5, alignY = 0.5;
        if (element.Get("preserveAspectRatio") is { } preserve && !TryParsePreserveAspectRatio(preserve, out uniform, out alignX, out alignY, out slice))
        {
            Issue(SvgIssueKind.Invalid, "preserveAspectRatio");
            (uniform, alignX, alignY, slice) = (true, 0.5, 0.5, false);
        }

        ReadOnlyMemory<byte> data = default;
        string? mediaType = null, source = null;
        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // Decoded once an element: a use can ask for the same picture any number of times.
            SvgElementExtras extras = element.Extras ??= new SvgElementExtras();
            if (!extras.PictureRead)
            {
                if (TryDataUri(href, out byte[]? bytes, out string? type)) extras.Picture = bytes;
                extras.MediaType = type;
                extras.PictureRead = true;
            }
            (data, mediaType) = (extras.Picture, extras.MediaType);
            if (data.IsEmpty)
            {
                Issue(SvgIssueKind.Invalid, "image: a data URI that cannot be read");
                return;
            }
            if (mediaType is not null && mediaType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
            {
                Issue(SvgIssueKind.Unsupported, "image that is an SVG document");
                return;
            }
        }
        else if (href[0] == '#')
        {
            Issue(SvgIssueKind.Invalid, "image: a reference to an element, not to a picture");
            return;
        }
        else
        {
            // Another file. Its address is kept and nothing is fetched: whether and where from is the caller's decision.
            source = href;
        }

        bool layered = style.ClipPath is not null || style.Mask is not null || style.Filter is not null || style.Blend != VectorBlendMode.Normal;
        var image = new VectorImage
        {
            Id = element.Get("id"),
            Data = data,
            MediaType = mediaType,
            Source = source,
            Bounds = new VectorRect((float)x, (float)y, (float)w, (float)h),
            Transform = matrix.ToMatrix(),
            // One picture cannot overlap itself, so the element's opacity is simply its own.
            Opacity = layered ? 1f : (float)style.Opacity,
            Fit = !uniform ? ImageFit.Stretch : slice ? ImageFit.Slice : ImageFit.Meet,
            AlignX = (float)alignX,
            AlignY = (float)alignY,
        };
        if (!layered)
        {
            output.Add(image);
            return;
        }
        EndLayer([image], output, style, null, matrix, viewport, depth);
    }

    /// <summary>
    /// The bytes and media type of a <c>data:</c> URI: <c>data:[type][;parameter]*[;base64],data</c>. Base64 may be
    /// broken over lines, as it is in files written by hand or by a tool that wraps; the other form is percent-encoded.
    /// </summary>
    internal static bool TryDataUri(ReadOnlySpan<char> uri, out byte[]? bytes, out string? mediaType)
    {
        bytes = null;
        mediaType = null;
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        uri = uri[5..];
        int comma = uri.IndexOf(',');
        if (comma < 0) return false;
        ReadOnlySpan<char> header = uri[..comma], payload = uri[(comma + 1)..];

        bool base64 = false;
        bool first = true;
        foreach (Range range in header.Split(';'))
        {
            ReadOnlySpan<char> part = header[range].Trim();
            if (first)
            {
                if (!part.IsEmpty) mediaType = part.ToString().ToLowerInvariant();
                first = false;
            }
            else if (part.Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                base64 = true;
            }
        }

        if (base64)
        {
            // Whitespace out, percent-escapes (%2B for +, %0A for a line break) undone, then decoded.
            char[] text = new char[payload.Length];
            int n = 0;
            for (int i = 0; i < payload.Length; i++)
            {
                char ch = payload[i];
                if (ch == '%' && i + 2 < payload.Length && TryHex(payload[i + 1], payload[i + 2], out byte escaped))
                {
                    ch = (char)escaped;
                    i += 2;
                }
                if (!char.IsWhiteSpace(ch)) text[n++] = ch;
            }
            byte[] buffer = new byte[n / 4 * 3 + 3];
            if (!Convert.TryFromBase64Chars(text.AsSpan(0, n), buffer, out int written))
            {
                // Padding left off is common and harmless.
                int padded = (n + 3) / 4 * 4;
                if (padded == n || padded > text.Length + 3) return false;
                Array.Resize(ref text, padded);
                for (int i = n; i < padded; i++) text[i] = '=';
                if (!Convert.TryFromBase64Chars(text.AsSpan(0, padded), buffer, out written)) return false;
            }
            if (written == 0) return false;
            bytes = buffer.AsSpan(0, written).ToArray();
            return true;
        }

        var raw = new List<byte>(payload.Length);
        for (int i = 0; i < payload.Length; i++)
        {
            char ch = payload[i];
            if (ch == '%' && i + 2 < payload.Length && TryHex(payload[i + 1], payload[i + 2], out byte escaped))
            {
                raw.Add(escaped);
                i += 2;
            }
            else if (ch < 256)
            {
                raw.Add((byte)ch);
            }
            else
            {
                return false;
            }
        }
        if (raw.Count == 0) return false;
        bytes = [.. raw];
        return true;

        static bool TryHex(char high, char low, out byte value)
        {
            value = 0;
            if (!char.IsAsciiHexDigit(high) || !char.IsAsciiHexDigit(low)) return false;
            value = (byte)(Digit(high) << 4 | Digit(low));
            return true;

            static int Digit(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
        }
    }
}
