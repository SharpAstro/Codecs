namespace SharpAstro.Jxl;

/// <summary>
/// Decodes a minimal VarDCT JPEG XL frame (the inverse of <see cref="JxlVarDctEncoder"/>) back to
/// 8-bit RGB. It threads the same section readers the encoder's writers mirror — LfGlobal
/// (<see cref="JxlLfGlobalVarDct"/>) + GlobalModular tree (<see cref="JxlMaConfig"/>),
/// <see cref="JxlLfGroup"/>, <see cref="JxlHfGlobal"/>, and the PassGroup HF coefficients
/// (<see cref="JxlHfCoeff"/>) — then reconstructs pixels via <see cref="JxlVarDctImage"/>.
///
/// <para>
/// Handles single-group frames (one combined TOC section, ≤256 px) and multi-group frames (one LF
/// group, ≤2048 px, with one byte-aligned PassGroup section per 256×256 region). It does not apply
/// adaptive-LF-smoothing or the restoration filters, so it matches our own (filter-free) encode but is
/// not a general libjxl-compatible VarDCT decoder. All-DCT8, single LF group, xyb path only.
/// </para>
/// </summary>
internal static class JxlVarDctFrame
{
    private const int GroupBlocks = 32; // 256-px group edge in 8×8 blocks

    public static int[][] DecodeToRgb24(byte[] containerOrCodestream)
    {
        byte[] cs = JxlContainer.ExtractCodestream(containerOrCodestream);
        var br = new JxlBitReader(cs.AsSpan(2)); // skip FF 0A

        (int width, int height) = JxlSizeHeader.Read(ref br);
        JxlImageMetadata meta = JxlImageMetadata.Read(ref br);
        JxlFrameHeader frame = JxlFrameHeader.Read(ref br, meta, width, height);
        if (frame.Encoding != JxlFrameEncoding.VarDct)
            throw new NotSupportedException("JPEG XL: expected a VarDCT frame.");
        if (frame.NumLfGroups != 1)
            throw new NotSupportedException("JPEG XL: only single-LF-group VarDCT frames are supported.");
        JxlToc toc = JxlToc.Read(ref br, frame);

        int bw = (width + 7) / 8, bh = (height + 7) / 8;
        int cfW = (width + 63) / 64, cfH = (height + 63) / 64;
        int gpr = (bw + GroupBlocks - 1) / GroupBlocks;
        int numGroups = frame.NumGroups;
        int numChannels = 3 + meta.NumExtraChannels;
        int nodeLimit = (int)Math.Min(1 << 22, 1024 + (long)width * height * numChannels / 16);

        var hfGrid = new int[3][];
        for (int c = 0; c < 3; c++)
            hfGrid[c] = new int[width * height];

        JxlLfGlobalVarDct lfVarDct;
        JxlLfGroup lfGroup;

        if (toc.EntryCount == 1)
        {
            // Single combined section: everything is bit-contiguous from the current position.
            (lfVarDct, JxlMaConfig tree) = ParseLfGlobal(ref br, nodeLimit);
            lfGroup = JxlLfGroup.Read(ref br, tree, bw, bh, cfW, cfH, lfStreamIndex: 1, hfStreamIndex: 3);
            JxlHfGlobal hfGlobal = JxlHfGlobal.Read(ref br, numGroups, lfVarDct.NumBlockClusters);
            DecodePassGroup(ref br, hfGlobal, lfGroup.BlockGrid, bw, groupIdx: 0, gpr: 1, hfGrid);
        }
        else
        {
            // Multi-entry TOC: LfGlobal, LfGroup, HfGlobal, then one PassGroup per group — each a
            // separate byte-aligned section. Slice the codestream at the cumulative offsets.
            int secStart = 2 + (int)br.BytesRead;
            var offsets = new int[toc.EntryCount];
            int acc = secStart;
            for (int i = 0; i < toc.EntryCount; i++) { offsets[i] = acc; acc += (int)toc.Sizes[i]; }

            var lfgBr = new JxlBitReader(cs.AsSpan(offsets[0], (int)toc.Sizes[0]));
            (lfVarDct, JxlMaConfig tree) = ParseLfGlobal(ref lfgBr, nodeLimit);

            var lggBr = new JxlBitReader(cs.AsSpan(offsets[1], (int)toc.Sizes[1]));
            lfGroup = JxlLfGroup.Read(ref lggBr, tree, bw, bh, cfW, cfH, lfStreamIndex: 1, hfStreamIndex: 3);

            var hfgBr = new JxlBitReader(cs.AsSpan(offsets[2], (int)toc.Sizes[2]));
            JxlHfGlobal hfGlobal = JxlHfGlobal.Read(ref hfgBr, numGroups, lfVarDct.NumBlockClusters);

            for (int g = 0; g < numGroups; g++)
            {
                int idx = 3 + g;
                var pgBr = new JxlBitReader(cs.AsSpan(offsets[idx], (int)toc.Sizes[idx]));
                DecodePassGroup(ref pgBr, hfGlobal, lfGroup.BlockGrid, bw, g, gpr, hfGrid);
            }
        }

        // Reconstruct pixels. LfQuant is in modular order [Y, X, B]; map back to physical [X, Y, B].
        int hfMul = lfGroup.BlockGrid[0].HfMul;
        var encoded = new JxlVarDctImage.Encoded
        {
            Width = width, Height = height, Bw = bw, Bh = bh,
            GlobalScale = lfVarDct.GlobalScale, QuantLf = lfVarDct.QuantLf,
            HfMul = hfMul, ExtraPrecision = lfGroup.ExtraPrecision,
            LfQuant = [lfGroup.LfQuant[1], lfGroup.LfQuant[0], lfGroup.LfQuant[2]],
            HfQuant = hfGrid,
        };
        float[][] srgb = JxlVarDctImage.Decode(encoded);

        var rgb = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            rgb[c] = new int[width * height];
            for (int i = 0; i < width * height; i++)
                rgb[c][i] = (int)MathF.Round(Math.Clamp(srgb[c][i], 0f, 1f) * 255f);
        }
        return rgb;
    }

    private static (JxlLfGlobalVarDct LfVarDct, JxlMaConfig Tree) ParseLfGlobal(ref JxlBitReader br, int nodeLimit)
    {
        if (!br.ReadBit()) // LfChannelDequantization all_default
            for (int i = 0; i < 3; i++) br.ReadBits(16);
        JxlLfGlobalVarDct lfVarDct = JxlLfGlobalVarDct.Read(ref br);

        if (!br.ReadBit()) // global_tree_present
            throw new NotSupportedException("JPEG XL: VarDCT frame without a global tree is not supported.");
        JxlMaConfig tree = JxlMaConfig.Parse(ref br, nodeLimit);
        return (lfVarDct, tree);
    }

    /// <summary>Decode one PassGroup's HF coefficients (a ≤32×32-block region) into the full grid.</summary>
    private static void DecodePassGroup(
        ref JxlBitReader br, JxlHfGlobal hfGlobal, JxlBlockInfo[] blockGrid, int bw, int groupIdx, int gpr, int[][] hfGrid)
    {
        int bh = blockGrid.Length / bw;
        int gcol = groupIdx % gpr, grow = groupIdx / gpr;
        int bx0 = gcol * GroupBlocks, by0 = grow * GroupBlocks;
        int bwG = Math.Min(GroupBlocks, bw - bx0);
        int bhG = Math.Min(GroupBlocks, bh - by0);

        var subGrid = new JxlBlockInfo[bwG * bhG];
        for (int y = 0; y < bhG; y++)
            for (int x = 0; x < bwG; x++)
                subGrid[y * bwG + x] = blockGrid[(by0 + y) * bw + (bx0 + x)];

        int subW = bwG * 8;
        var subCoeff = new int[3][];
        for (int c = 0; c < 3; c++)
            subCoeff[c] = new int[subW * (bhG * 8)];

        var p = new JxlHfCoeff.Params
        {
            Bw = bwG, Bh = bhG, BlockGrid = subGrid,
            NumBlockClusters = 15, BlockCtxMap = JxlLfGlobalVarDct.DefaultBlockCtxMap, // our encoder's default HfBlockContext
        };
        JxlHfCoeff.Decode(ref br, hfGlobal.HfDist, hfGlobal.HfDist.ClusterMap, hfGlobal.NumHfPresets, p, subCoeff);

        int gridW = bw * 8;
        for (int c = 0; c < 3; c++)
            for (int py = 0; py < bhG * 8; py++)
                for (int px = 0; px < bwG * 8; px++)
                    hfGrid[c][(by0 * 8 + py) * gridW + bx0 * 8 + px] = subCoeff[c][py * subW + px];
    }
}
