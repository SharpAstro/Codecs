namespace SharpAstro.Jxl;

/// <summary>
/// Encodes a still-image lossless Modular JPEG XL codestream (ISO/IEC 18181-1 §F/§H) — the
/// inverse of <see cref="JxlModularFrame"/>. Emits the minimal valid structure libjxl accepts:
/// SizeHeader → ImageMetadata → FrameHeader (Modular, single group, single pass) → TOC → one
/// section holding LfGlobal + GlobalModular with a <b>single-leaf MA tree</b> (Gradient
/// predictor, multiplier 1, offset 0) and one even-distribution sample stream. No RCT/Palette
/// yet (channels stored directly), so correctness — not ratio — is the goal. The tree and the
/// sample residuals both ride on the validated <see cref="JxlEntropyEncoder"/>.
/// </summary>
internal static class JxlModularEncoder
{
    // Hybrid-uint configs. Keep split_exponent < log_alphabet_size (8) so msb/lsb survive
    // serialisation, and small enough that the largest residual maps to a token < 256.
    private static readonly JxlIntegerConfig TreeConfig = JxlIntegerConfig.Create(4, 0, 0);
    private static readonly JxlIntegerConfig SampleConfig = JxlIntegerConfig.Create(4, 0, 0);

    /// <summary>
    /// Encodes <paramref name="channels"/> (one row-major integer grid per colour channel, length
    /// <c>width*height</c>) as a bare .jxl codestream. <paramref name="grayscale"/> selects 1 vs 3
    /// colour channels; <paramref name="bitsPerSample"/> is the integer sample precision.
    /// </summary>
    public static byte[] Encode(int[][] channels, int width, int height, int bitsPerSample, bool grayscale)
    {
        int colorChannels = grayscale ? 1 : 3;
        if (channels.Length != colorChannels)
            throw new ArgumentException($"Expected {colorChannels} channels, got {channels.Length}.", nameof(channels));
        foreach (int[] ch in channels)
            if (ch.Length != width * height)
                throw new ArgumentException("Channel length does not match width*height.", nameof(channels));
        if (width > 1024 || height > 1024)
            throw new NotSupportedException("JPEG XL encoder: multi-group images (dim > 1024) are not yet supported.");

        // group_size_shift: smallest shift whose group dim (128<<shift) covers the image => 1 group.
        int groupSizeShift = 0;
        while (groupSizeShift < 3 && (128 << groupSizeShift) < Math.Max(width, height))
            groupSizeShift++;

        byte[] section = BuildSection(channels, width, height);

        var bw = new JxlBitWriter();
        bw.WriteBits(0xFF, 8);
        bw.WriteBits(0x0A, 8); // codestream signature FF 0A

        WriteSizeHeader(bw, width, height);
        WriteImageMetadata(bw, bitsPerSample, grayscale);
        WriteFrameHeader(bw, groupSizeShift);
        WriteToc(bw, section.Length);

        bw.WriteBytes(section);
        return bw.ToArray();
    }

    // ---- codestream header chain (inverses of the matching Read methods) ----

    private static void WriteSizeHeader(JxlBitWriter bw, int width, int height)
    {
        bw.WriteBit(false);              // div8 = false -> general dimension coding
        WriteDimension(bw, height);
        bw.WriteBits(0, 3);              // ratio = 0 -> explicit width
        WriteDimension(bw, width);
    }

    private static void WriteDimension(JxlBitWriter bw, int value) =>
        bw.WriteU32((uint)(value - 1), (0, 9), (0, 13), (0, 18), (0, 30));

    private static void WriteImageMetadata(JxlBitWriter bw, int bitsPerSample, bool grayscale)
    {
        bw.WriteBit(false); // all_default = false (we are not XYB)
        bw.WriteBit(false); // extra_fields = false

        new JxlBitDepth { FloatingPoint = false, BitsPerSample = bitsPerSample, ExponentBits = 0 }.Write(bw);
        bw.WriteBit(bitsPerSample <= 16);        // modular_16bit_buffers
        bw.WriteU32(0, (0, 0), (1, 0), (2, 4), (1, 12)); // num_extra_channels = 0
        bw.WriteBit(false);                       // xyb_encoded = false

        WriteColorEncoding(bw, grayscale);
        bw.WriteU64(0);    // extensions (none)
        bw.WriteBit(true); // default_m
    }

    // Inverse of JxlColorEncoding.Read: sRGB for colour, an explicit D65/sRGB grey otherwise.
    private static void WriteColorEncoding(JxlBitWriter bw, bool grayscale)
    {
        if (!grayscale)
        {
            bw.WriteBit(true); // all_default -> sRGB RGB
            return;
        }
        bw.WriteBit(false);                       // all_default
        bw.WriteBit(false);                       // want_icc
        bw.WriteEnum((uint)JxlColorSpace.Gray);   // color_space
        bw.WriteEnum(1);                          // white_point = D65
        // grey -> no primaries
        bw.WriteBit(false);                       // have_gamma -> transfer_function follows
        bw.WriteEnum(13);                         // transfer_function = sRGB
        bw.WriteEnum(0);                          // rendering_intent = Perceptual
    }

    private static void WriteFrameHeader(JxlBitWriter bw, int groupSizeShift)
    {
        bw.ZeroPadToByte();
        bw.WriteBit(false);                              // all_default = false
        bw.WriteBits((uint)JxlFrameType.RegularFrame, 2);
        bw.WriteBits((uint)JxlFrameEncoding.Modular, 1);
        bw.WriteU64(0);                                  // flags
        bw.WriteBit(false);                              // do_ycbcr (xyb=false so the bit is present)
        bw.WriteU32(1, (1, 0), (2, 0), (4, 0), (8, 0));  // upsampling = 1
        bw.WriteBits((uint)groupSizeShift, 2);           // group_size_shift (Modular)
        bw.WriteU32(1, (1, 0), (2, 0), (3, 0), (4, 3));  // num_passes = 1
        bw.WriteBit(false);                              // have_crop = false
        bw.WriteU32(0, (0, 0), (1, 0), (2, 0), (3, 2));  // blending mode = Replace
        bw.WriteBit(true);                               // is_last = true
        bw.WriteU32(0, (0, 0), (0, 4), (16, 5), (48, 10)); // name length = 0

        // Restoration filter: the all_default block enables Gabor + 2 EPF iterations, both LOSSY
        // smoothing passes that corrupt a lossless image (a gradient is visibly altered; a flat
        // solid is not — which is why solid round-trips but gradients don't). Disable both.
        bw.WriteBit(false); // restoration filter all_default = false
        bw.WriteBit(false); // gab_enabled = false
        bw.WriteBits(0, 2); // epf_iters = 0
        bw.WriteU64(0);     // restoration-filter extensions (none)

        bw.WriteU64(0);     // frame-header extensions (none)
    }

    private static void WriteToc(JxlBitWriter bw, int sectionLength)
    {
        bw.WriteBit(false); // permuted = false (immediately after the frame header, not byte-aligned)
        bw.ZeroPadToByte();
        bw.WriteU32((uint)sectionLength, (0, 10), (1024, 14), (17408, 22), (4211712, 30));
        bw.ZeroPadToByte();
    }

    // ---- the single TOC section: LfGlobal + GlobalModular + tree + samples ----

    private static byte[] BuildSection(int[][] channels, int width, int height)
    {
        var sec = new JxlBitWriter();

        // LfGlobal: no patches/splines/noise (flags 0), LfChannelDequantization all_default.
        sec.WriteBit(true);  // LfChannelDequantization all_default

        // GlobalModular.
        sec.WriteBit(false); // global_tree_present = false

        // Local modular header.
        sec.WriteBit(false); // use_global_tree = false
        sec.WriteBit(true);  // wp default_wp
        sec.WriteU32(0, (0, 0), (1, 0), (2, 4), (18, 8)); // nb_transforms = 0

        WriteSingleLeafTree(sec);
        WriteSamples(sec, channels, width, height);

        return sec.ToArray();
    }

    // A one-node MA tree: property==0 marks a leaf; then predictor / offset / mul_log / mul_bits.
    // Tree-decoder contexts: 0=value, 1=property, 2=predictor, 3=offset, 4=mul_log, 5=mul_bits.
    private static void WriteSingleLeafTree(JxlBitWriter sec)
    {
        var treeStream = new (int Ctx, uint Value)[]
        {
            (1, 0),                                 // property = 0 -> leaf
            (2, (uint)JxlPredictor.Gradient),       // predictor
            (3, 0),                                 // offset (packed) = 0
            (4, 0),                                 // mul_log = 0
            (5, 0),                                 // mul_bits = 0  -> multiplier (0+1)<<0 = 1
        };
        // All six tree contexts share one cluster (one histogram covers the tiny token set).
        var tree = new JxlEntropyEncoder([0, 0, 0, 0, 0, 0], [TreeConfig]);
        tree.Encode(sec, treeStream);
    }

    private static void WriteSamples(JxlBitWriter sec, int[][] channels, int width, int height)
    {
        var stream = new List<(int Ctx, uint Value)>(channels.Length * width * height);
        foreach (int[] grid in channels)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    // Neighbours, exactly as JxlModularImage.Decode reconstructs them (lossless =>
                    // the original samples equal the reconstructed grid the decoder will see).
                    int n = y > 0 ? grid[(y - 1) * width + x] : (x > 0 ? grid[x - 1] : 0);
                    int w = x > 0 ? grid[y * width + x - 1] : (y > 0 ? grid[(y - 1) * width] : 0);
                    int nw = y > 0
                        ? (x > 0 ? grid[(y - 1) * width + x - 1] : grid[(y - 1) * width])
                        : (x > 0 ? grid[x - 1] : 0);

                    int pred = JxlModular.GradClamped(n, w, nw); // Gradient predictor
                    int sample = grid[y * width + x];
                    stream.Add((0, PackSigned(sample - pred)));
                }
        }

        // Single leaf -> single distribution; no context map (num_dist == 1).
        var samples = new JxlEntropyEncoder([0], [SampleConfig]);
        samples.Encode(sec, stream);
    }

    /// <summary>Inverse of <see cref="JxlModular.UnpackSigned"/> (zigzag encode).</summary>
    private static uint PackSigned(int value) => (uint)((value << 1) ^ (value >> 31));
}
