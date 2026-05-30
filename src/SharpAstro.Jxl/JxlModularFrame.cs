namespace SharpAstro.Jxl;

/// <summary>Decoded Modular channels (row-major integer samples) plus geometry.</summary>
internal sealed class JxlModularDecodeResult
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int ColorChannels { get; init; }
    public required int BitsPerSample { get; init; }
    public required int[][] Channels { get; init; } // [colorChannels][width*height]
}

/// <summary>
/// Decodes a still-image lossless Modular JPEG XL frame end-to-end (ISO/IEC 18181-1 §F/§H):
/// SizeHeader → ImageMetadata → FrameHeader → TOC → LfGlobal (preamble + GlobalModular) → channel
/// decode → inverse transforms. Currently limited to the single-group, non-XYB, non-YCbCr path
/// (RGB or grey, no extra channels, RCT only) — the shape Magick/libjxl emits for small lossless
/// images. Wider support (Squeeze/Palette, multi-group, extra channels) is added incrementally.
/// </summary>
internal static class JxlModularFrame
{
    public static JxlModularDecodeResult Decode(byte[] containerOrCodestream)
    {
        byte[] cs = JxlContainer.ExtractCodestream(containerOrCodestream);
        var br = new JxlBitReader(cs.AsSpan(2)); // skip FF 0A

        (int width, int height) = JxlSizeHeader.Read(ref br);
        JxlImageMetadata meta = JxlImageMetadata.Read(ref br);
        JxlFrameHeader frame = JxlFrameHeader.Read(ref br, meta, width, height);
        if (frame.Encoding != JxlFrameEncoding.Modular)
            throw new NotSupportedException("JPEG XL: only Modular (lossless) frames are supported.");
        JxlToc toc = JxlToc.Read(ref br, frame);
        if (toc.EntryCount != 1)
            throw new NotSupportedException("JPEG XL: only single-group Modular frames are supported.");

        // LfGlobal preamble.
        if ((frame.Flags & 0x02) != 0) throw new NotSupportedException("JPEG XL patches are not yet supported.");
        if ((frame.Flags & 0x10) != 0) throw new NotSupportedException("JPEG XL splines are not yet supported.");
        if ((frame.Flags & 0x01) != 0) throw new NotSupportedException("JPEG XL noise is not yet supported.");
        if (!br.ReadBit()) // LfChannelDequantization all_default
            for (int i = 0; i < 3; i++) br.ReadBits(16); // three F16 weights (unused for Modular)

        if (frame.DoYcbcr) throw new NotSupportedException("JPEG XL do_ycbcr Modular is not yet supported.");
        if (meta.NumExtraChannels != 0) throw new NotSupportedException("JPEG XL Modular extra channels are not yet supported.");

        bool grayscale = meta.ColorSpace == JxlColorSpace.Gray && !meta.XybEncoded;
        int colorChannels = grayscale ? 1 : 3;
        long maxNodes = Math.Min(1 << 22, 1024 + (long)width * height * colorChannels / 16);
        int nodeLimit = (int)maxNodes;

        // GlobalModular.
        bool globalTreePresent = br.ReadBit();
        JxlMaConfig? globalTree = globalTreePresent ? JxlMaConfig.Parse(ref br, nodeLimit) : null;

        int colorWidth = frame.ColorWidth, colorHeight = frame.ColorHeight;
        var channels = new JxlModularChannel[colorChannels];
        for (int i = 0; i < colorChannels; i++)
            channels[i] = new JxlModularChannel(colorWidth, colorHeight);

        JxlModularHeader header = JxlModularHeader.Parse(ref br);
        JxlMaConfig tree = header.UseGlobalTree
            ? globalTree ?? throw new InvalidDataException("JPEG XL: local modular requests a global tree, but none is present.")
            : JxlMaConfig.Parse(ref br, nodeLimit);

        foreach (JxlTransform t in header.Transforms)
            if (t.Type != JxlTransformType.Rct)
                throw new NotSupportedException($"JPEG XL Modular transform {t.Type} is not yet supported.");

        JxlModularImage.Decode(ref br, tree, channels, header.Wp, streamIndex: 0);

        // Inverse transforms in reverse order (RCT only). RCT may permute the channel array.
        var grids = new int[colorChannels][];
        for (int i = 0; i < colorChannels; i++)
            grids[i] = channels[i].Data;
        for (int i = header.Transforms.Length - 1; i >= 0; i--)
        {
            JxlTransform t = header.Transforms[i];
            if (t.Type == JxlTransformType.Rct)
                JxlRct.Inverse(t.RctType, grids, t.BeginC);
        }

        return new JxlModularDecodeResult
        {
            Width = colorWidth,
            Height = colorHeight,
            ColorChannels = colorChannels,
            BitsPerSample = meta.BitDepth.BitsPerSample,
            Channels = grids,
        };
    }
}
