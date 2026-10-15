namespace SharpAstro.Webp;

/// <summary>
/// Decodes still and animated WebP images. Spec-derived from RFC 6386 / RFC 9649;
/// libwebp/libvpx source was not consulted — their binaries are used only as test
/// oracles. Written with AI assistance, whose training data may include libwebp.
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

    /// <summary>Decodes every frame, returning the full composited canvas for each one.</summary>
    /// <param name="data">A still or animated WebP image.</param>
    public static WebpAnimation DecodeAnimation(ReadOnlySpan<byte> data)
    {
        WebpContainer container = WebpContainer.Read(data);
        var decoder = new WebpAnimationDecoder(data.ToArray());
        byte[] canvas = new byte[checked(decoder.CanvasWidth * decoder.CanvasHeight * 4)];
        var frames = new List<WebpFrame>(decoder.FrameCount);
        while (decoder.TryDecodeNextFrame(canvas, out WebpFrameInfo frame))
        {
            frames.Add(new WebpFrame(canvas.ToArray(), frame.TimestampMs, frame.DurationMs));
        }

        return new WebpAnimation(
            decoder.CanvasWidth,
            decoder.CanvasHeight,
            decoder.LoopCount,
            decoder.BackgroundColor,
            frames.AsReadOnly())
        {
            IccProfile = container.HasIccProfile ? container.IccProfile.ToArray() : null,
            Exif = container.HasExif ? container.Exif.ToArray() : null,
            Xmp = container.HasXmp ? container.Xmp.ToArray() : null,
        };
    }

    public static WebpImage Decode(ReadOnlySpan<byte> data)
    {
        WebpContainer container = WebpContainer.Read(data);
        WebpInfo info = container.Info;
        byte[] rgba;
        if (info.Format == WebpFormat.Animated)
        {
            rgba = DecodeFirstAnimationFrame(data, info.Width, info.Height);
        }
        else if (info.Format == WebpFormat.Lossless)
        {
            rgba = Vp8LDecoder.DecodeRgba(container.Vp8Bitstream);
        }
        else
        {
            rgba = new byte[checked(info.Width * info.Height * 4)];
            Vp8Yuv yuv = Vp8Decoder.Decode(container.Vp8Bitstream, info.Width, info.Height);
            WriteRgba(yuv, rgba, checked(info.Width * 4), fancy: true);
            if (container.HasAlphaChunk)
            {
                ApplyAlpha(container.AlphaData, rgba, info.Width, info.Height);
            }
        }

        byte[]? icc = container.HasIccProfile ? container.IccProfile.ToArray() : null;
        byte[]? exif = container.HasExif ? container.Exif.ToArray() : null;
        byte[]? xmp = container.HasXmp ? container.Xmp.ToArray() : null;
        return new WebpImage(info.Width, info.Height, info.HasAlpha, rgba, icc, exif, xmp);
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
        if (info.Format == WebpFormat.Animated)
        {
            byte[] firstFrame = DecodeFirstAnimationFrame(data, info.Width, info.Height, fancy);
            for (int row = 0; row < info.Height; row++)
            {
                firstFrame.AsSpan(row * minimumStride, minimumStride)
                    .CopyTo(rgba.Slice(row * stride, minimumStride));
            }

            return;
        }

        if (info.Format == WebpFormat.Lossless)
        {
            byte[] decoded = Vp8LDecoder.DecodeRgba(container.Vp8Bitstream);
            for (int row = 0; row < info.Height; row++)
            {
                decoded.AsSpan(row * minimumStride, minimumStride)
                    .CopyTo(rgba.Slice(row * stride, minimumStride));
            }
        }
        else
        {
            Vp8Yuv yuv = Vp8Decoder.Decode(container.Vp8Bitstream, info.Width, info.Height);
            WriteRgba(yuv, rgba, stride, fancy);
            if (container.HasAlphaChunk)
            {
                ApplyAlpha(container.AlphaData, rgba, info.Width, info.Height, stride);
            }
        }
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
        if (container.Info.Format != WebpFormat.Lossy)
        {
            throw new NotSupportedException("VP8 YUV output is unavailable for lossless WebP.");
        }

        return Vp8Decoder.Decode(container.Vp8Bitstream, container.Info.Width, container.Info.Height, applyLoopFilter);
    }

    internal static void DecodeToNoFancy(ReadOnlySpan<byte> data, Span<byte> rgba)
    {
        DecodeTo(data, rgba, 0, fancy: false);
    }

    internal static byte[] DecodeFrameRgba(
        ReadOnlySpan<byte> bitstream,
        WebpFormat format,
        ReadOnlySpan<byte> alphaData,
        int width,
        int height,
        bool fancy = true)
    {
        if (format == WebpFormat.Lossless)
        {
            if (!alphaData.IsEmpty)
            {
                throw new InvalidDataException("A VP8L animation frame cannot have a separate ALPH chunk.");
            }

            return Vp8LDecoder.DecodeRgba(bitstream);
        }

        Vp8Yuv yuv = Vp8Decoder.Decode(bitstream, width, height);
        byte[] rgba = new byte[checked(width * height * 4)];
        WriteRgba(yuv, rgba, checked(width * 4), fancy);
        if (!alphaData.IsEmpty)
        {
            ApplyAlpha(alphaData, rgba, width, height);
        }

        return rgba;
    }

    private static byte[] DecodeFirstAnimationFrame(
        ReadOnlySpan<byte> data,
        int width,
        int height,
        bool fancy = true)
    {
        var decoder = new WebpAnimationDecoder(data.ToArray(), fancy);
        byte[] rgba = new byte[checked(width * height * 4)];
        if (!decoder.TryDecodeNextFrame(rgba, out _))
        {
            throw new InvalidDataException("The WebP animation has no frames.");
        }

        return rgba;
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

    private static void ApplyAlpha(
        ReadOnlySpan<byte> alphaChunk,
        Span<byte> rgba,
        int width,
        int height,
        int stride = 0)
    {
        if (alphaChunk.Length < 1)
        {
            throw new InvalidDataException("The WebP ALPH chunk is empty.");
        }

        if (stride == 0)
        {
            stride = checked(width * 4);
        }

        int header = alphaChunk[0];
        int compression = header & 3;
        int filter = (header >> 2) & 3;
        byte[] values;
        if (compression == 0)
        {
            int expected = checked(width * height);
            if (alphaChunk.Length - 1 != expected)
            {
                throw new InvalidDataException("The raw WebP alpha stream has the wrong size.");
            }

            values = alphaChunk[1..].ToArray();
        }
        else if (compression == 1)
        {
            values = Vp8LDecoder.DecodeAlpha(alphaChunk[1..], width, height);
        }
        else
        {
            throw new InvalidDataException("The WebP alpha compression method is unsupported.");
        }

        if (filter != 0)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    int left = x == 0 ? 0 : values[index - 1];
                    int above = y == 0 ? 0 : values[index - width];
                    int topLeft = x == 0 || y == 0 ? 0 : values[index - width - 1];
                    int predictor = filter switch
                    {
                        1 => x == 0 ? above : left,
                        2 => y == 0 ? left : above,
                        3 => x == 0 ? above : y == 0 ? left : Math.Clamp(left + above - topLeft, 0, 255),
                        _ => 0,
                    };
                    values[index] = (byte)(values[index] + predictor);
                }
            }
        }

        for (int y = 0; y < height; y++)
        {
            Span<byte> row = rgba.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                row[x * 4 + 3] = values[y * width + x];
            }
        }
    }

    // Fitted black-box to dwebp; approximately BT.601 limited-range with 14-bit coefficients.
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
