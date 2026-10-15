namespace SharpAstro.Webp;

/// <summary>
/// Decodes still lossy WebP images. The implementation is derived from RFC 6386
/// and RFC 9649 prose and mathematics, with standard specification tables copied
/// as data. No libwebp or libvpx source was consulted or transcribed; their
/// command-line binaries are used only as black-box test oracles.
/// </summary>
public static class WebpDecoder
{
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out WebpInfo info)
    {
        try
        {
            info = WebpContainer.Read(data).Info;
            return true;
        }
        catch (Exception)
        {
            info = default;
            return false;
        }
    }

    public static WebpInfo ReadInfo(ReadOnlySpan<byte> data) => WebpContainer.Read(data).Info;

    public static WebpImage Decode(ReadOnlySpan<byte> data)
    {
        WebpContainer container = WebpContainer.Read(data);
        WebpInfo info = container.Info;
        if (info.Format != WebpFormat.Lossy || info.HasAlpha || container.HasAlphaChunk)
        {
            throw new NotSupportedException("This WebP decode path lands in a later rung.");
        }

        Vp8Yuv yuv = Vp8Decoder.Decode(container.Vp8Bitstream, info.Width, info.Height);
        byte[] rgba = new byte[checked(info.Width * info.Height * 4)];
        WriteRgba(yuv, rgba, checked(info.Width * 4), fancy: true);
        byte[]? icc = container.IccProfile.IsEmpty ? null : container.IccProfile.ToArray();
        return new WebpImage(info.Width, info.Height, false, rgba, icc);
    }

    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride = 0)
    {
        DecodeTo(data, rgba, stride, fancy: true);
    }

    private static void DecodeTo(
        ReadOnlySpan<byte> data,
        Span<byte> rgba,
        int stride,
        bool fancy)
    {
        WebpInfo info = ReadInfo(data);
        int minimumStride = checked(info.Width * 4);
        if (stride == 0)
        {
            stride = minimumStride;
        }

        if (stride < minimumStride)
        {
            throw new ArgumentException("The stride is smaller than one RGBA row.", nameof(stride));
        }

        int requiredLength = checked((info.Height - 1) * stride + minimumStride);
        if (rgba.Length < requiredLength)
        {
            throw new ArgumentException("The destination is too small for the decoded image.", nameof(rgba));
        }

        WebpContainer container = WebpContainer.Read(data);
        if (info.Format != WebpFormat.Lossy || info.HasAlpha || container.HasAlphaChunk)
        {
            throw new NotSupportedException("This WebP decode path lands in a later rung.");
        }

        Vp8Yuv yuv = Vp8Decoder.Decode(container.Vp8Bitstream, info.Width, info.Height);
        WriteRgba(yuv, rgba, stride, fancy);
    }

    public static bool TryDecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride = 0)
    {
        try
        {
            DecodeTo(data, rgba, stride);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static Vp8Yuv DecodeYuv(ReadOnlySpan<byte> data, bool applyLoopFilter = true)
    {
        WebpContainer container = WebpContainer.Read(data);
        if (container.Info.Format != WebpFormat.Lossy || container.Info.HasAlpha || container.HasAlphaChunk)
        {
            throw new NotSupportedException("This WebP decode path lands in a later rung.");
        }

        return Vp8Decoder.Decode(container.Vp8Bitstream, container.Info.Width, container.Info.Height, applyLoopFilter);
    }

    internal static void DecodeToNoFancy(ReadOnlySpan<byte> data, Span<byte> rgba)
    {
        DecodeTo(data, rgba, 0, fancy: false);
    }

    private static void WriteRgba(Vp8Yuv yuv, Span<byte> output, int stride, bool fancy)
    {
        int width = yuv.Width;
        int height = yuv.Height;
        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;
        for (int row = 0; row < height; row++)
        {
            Span<byte> dst = output.Slice(row * stride, width * 4);
            int chromaRow = row >> 1;
            int adjacentRow = row % 2 == 0
                ? Math.Max(0, chromaRow - 1)
                : Math.Min(chromaHeight - 1, chromaRow + 1);
            for (int column = 0; column < width; column++)
            {
                int chromaColumn = column >> 1;
                int y = yuv.Y[row * width + column];
                int u;
                int v;
                if (fancy)
                {
                    int adjacentColumn = column % 2 == 0
                        ? Math.Max(0, chromaColumn - 1)
                        : Math.Min(chromaWidth - 1, chromaColumn + 1);
                    int current = chromaRow * chromaWidth + chromaColumn;
                    int horizontal = chromaRow * chromaWidth + adjacentColumn;
                    int vertical = adjacentRow * chromaWidth + chromaColumn;
                    int diagonal = adjacentRow * chromaWidth + adjacentColumn;
                    u = (9 * yuv.U[current] + 3 * yuv.U[horizontal] +
                        3 * yuv.U[vertical] + yuv.U[diagonal] + 8) >> 4;
                    v = (9 * yuv.V[current] + 3 * yuv.V[horizontal] +
                        3 * yuv.V[vertical] + yuv.V[diagonal] + 8) >> 4;
                }
                else
                {
                    int chroma = chromaRow * chromaWidth + chromaColumn;
                    u = yuv.U[chroma];
                    v = yuv.V[chroma];
                }
                ConvertYuvToRgb(y, u, v, out byte r, out byte g, out byte b);
                int offset = column * 4;
                dst[offset] = r;
                dst[offset + 1] = g;
                dst[offset + 2] = b;
                dst[offset + 3] = 255;
            }
        }
    }

    private static void ConvertYuvToRgb(int y, int u, int v, out byte red, out byte green, out byte blue)
    {
        // Black-box fit to dwebp (BT.601 limited-range, 14-bit coefficients); see
        // tests/SharpAstro.Codecs.Tests/Fixtures/webp/fit-yuv-to-rgb.py.
        int yTerm = MultiplyAndShift8(y, 19077);
        red = (byte)ClampByte((yTerm + MultiplyAndShift8(v, 26149) - 14234) >> 6);
        green = (byte)ClampByte(
            (yTerm - MultiplyAndShift8(u, 6419) - MultiplyAndShift8(v, 13320) + 8708) >> 6);
        blue = (byte)ClampByte((yTerm + MultiplyAndShift8(u, 33050) - 17685) >> 6);
    }

    private static int MultiplyAndShift8(int value, int coefficient) => value * coefficient >> 8;

    private static int ClampByte(int value) => Math.Clamp(value, 0, 255);
}
