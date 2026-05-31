namespace SharpAstro.Jxl;

/// <summary>
/// Decodes a minimal VarDCT JPEG XL frame (the inverse of <see cref="JxlVarDctEncoder"/>) back to
/// 8-bit RGB. It threads the same section readers the encoder's writers mirror — LfGlobal
/// (<see cref="JxlLfGlobalVarDct"/>) + GlobalModular tree (<see cref="JxlMaConfig"/>),
/// <see cref="JxlLfGroup"/>, <see cref="JxlHfGlobal"/>, and the PassGroup HF coefficients
/// (<see cref="JxlHfCoeff"/>) — then reconstructs pixels via <see cref="JxlVarDctImage"/>.
///
/// <para>
/// Handles single-group frames (one combined TOC section, ≤256 px) and multi-entry frames of arbitrary
/// size (one byte-aligned LfGroup section per 2048×2048 region, reassembled into the full LF-DC + block
/// grid, plus one byte-aligned PassGroup section per 256×256 region). It does not apply
/// adaptive-LF-smoothing or the restoration filters, so it matches our own (filter-free) encode but is
/// not a general libjxl-compatible VarDCT decoder. All-DCT8, xyb path only.
/// </para>
/// </summary>
internal static class JxlVarDctFrame
{
    private const int GroupBlocks = 32;    // 256-px group edge in 8×8 blocks
    private const int LfGroupBlocks = 256; // 2048-px LF-group edge in 8×8 blocks

    public static int[][] DecodeToRgb24(byte[] containerOrCodestream)
    {
        byte[] cs = JxlContainer.ExtractCodestream(containerOrCodestream);
        var br = new JxlBitReader(cs.AsSpan(2)); // skip FF 0A

        (int width, int height) = JxlSizeHeader.Read(ref br);
        JxlImageMetadata meta = JxlImageMetadata.Read(ref br);
        JxlFrameHeader frame = JxlFrameHeader.Read(ref br, meta, width, height);
        if (frame.Encoding != JxlFrameEncoding.VarDct)
            throw new NotSupportedException("JPEG XL: expected a VarDCT frame.");
        JxlToc toc = JxlToc.Read(ref br, frame);

        int bw = (width + 7) / 8, bh = (height + 7) / 8;
        int gpr = (bw + GroupBlocks - 1) / GroupBlocks;
        int lfgpr = (bw + LfGroupBlocks - 1) / LfGroupBlocks;
        int numGroups = frame.NumGroups;
        int numLfGroups = frame.NumLfGroups;
        int numChannels = 3 + meta.NumExtraChannels;
        int nodeLimit = (int)Math.Min(1 << 22, 1024 + (long)width * height * numChannels / 16);

        var hfGrid = new int[3][];
        var fullLfMod = new int[3][]; // modular channel order [Y, X, B], full bw×bh
        for (int c = 0; c < 3; c++)
        {
            hfGrid[c] = new int[width * height];
            fullLfMod[c] = new int[bw * bh];
        }
        var fullBlockGrid = new JxlBlockInfo[bw * bh];

        JxlLfGlobalVarDct lfVarDct;
        int extraPrecision;

        if (toc.EntryCount == 1)
        {
            // Single combined section (one LF group, one PassGroup): everything is bit-contiguous.
            (lfVarDct, JxlMaConfig tree) = ParseLfGlobal(ref br, nodeLimit);
            JxlLfGroup lg = ReadLfGroupRegion(ref br, tree, bw, bh, lfgpr, l: 0, numLfGroups: 1);
            extraPrecision = lg.ExtraPrecision;
            PlaceLfGroup(lg, 0, lfgpr, bw, fullLfMod, fullBlockGrid);
            JxlHfGlobal hfGlobal = JxlHfGlobal.Read(ref br, numGroups, lfVarDct.NumBlockClusters);
            DecodePassGroup(ref br, hfGlobal, fullBlockGrid, bw, groupIdx: 0, gpr: 1, hfGrid);
        }
        else
        {
            // Multi-entry TOC: LfGlobal, LfGroup×num_lf_groups, HfGlobal, PassGroup×num_groups — each a
            // separate byte-aligned section. Slice the codestream at the cumulative offsets.
            int secStart = 2 + (int)br.BytesRead;
            var offsets = new int[toc.EntryCount];
            int acc = secStart;
            for (int i = 0; i < toc.EntryCount; i++) { offsets[i] = acc; acc += (int)toc.Sizes[i]; }

            var lfgBr = new JxlBitReader(cs.AsSpan(offsets[0], (int)toc.Sizes[0]));
            (lfVarDct, JxlMaConfig tree) = ParseLfGlobal(ref lfgBr, nodeLimit);

            extraPrecision = 0;
            for (int l = 0; l < numLfGroups; l++)
            {
                int idx = 1 + l;
                var lggBr = new JxlBitReader(cs.AsSpan(offsets[idx], (int)toc.Sizes[idx]));
                JxlLfGroup lg = ReadLfGroupRegion(ref lggBr, tree, bw, bh, lfgpr, l, numLfGroups);
                extraPrecision = lg.ExtraPrecision;
                PlaceLfGroup(lg, l, lfgpr, bw, fullLfMod, fullBlockGrid);
            }

            int hfgIdx = 1 + numLfGroups;
            var hfgBr = new JxlBitReader(cs.AsSpan(offsets[hfgIdx], (int)toc.Sizes[hfgIdx]));
            JxlHfGlobal hfGlobal = JxlHfGlobal.Read(ref hfgBr, numGroups, lfVarDct.NumBlockClusters);

            for (int g = 0; g < numGroups; g++)
            {
                int idx = hfgIdx + 1 + g;
                var pgBr = new JxlBitReader(cs.AsSpan(offsets[idx], (int)toc.Sizes[idx]));
                DecodePassGroup(ref pgBr, hfGlobal, fullBlockGrid, bw, g, gpr, hfGrid);
            }
        }

        // Reconstruct pixels. LfQuant is modular order [Y, X, B]; map back to physical [X, Y, B].
        int hfMul = fullBlockGrid[0].HfMul;
        var encoded = new JxlVarDctImage.Encoded
        {
            Width = width, Height = height, Bw = bw, Bh = bh,
            GlobalScale = lfVarDct.GlobalScale, QuantLf = lfVarDct.QuantLf,
            HfMul = hfMul, ExtraPrecision = extraPrecision,
            LfQuant = [fullLfMod[1], fullLfMod[0], fullLfMod[2]],
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

    /// <summary>Read LF group <paramref name="l"/> (a ≤256×256-block region) using the shared tree.</summary>
    private static JxlLfGroup ReadLfGroupRegion(
        ref JxlBitReader br, JxlMaConfig tree, int bw, int bh, int lfgpr, int l, int numLfGroups)
    {
        int lcol = l % lfgpr, lrow = l / lfgpr;
        int bwL = Math.Min(LfGroupBlocks, bw - lcol * LfGroupBlocks);
        int bhL = Math.Min(LfGroupBlocks, bh - lrow * LfGroupBlocks);
        int cfW = (bwL * 8 + 63) / 64, cfH = (bhL * 8 + 63) / 64;
        return JxlLfGroup.Read(
            ref br, tree, bwL, bhL, cfW, cfH,
            lfStreamIndex: (uint)(1 + l), hfStreamIndex: (uint)(1 + 2 * numLfGroups + l));
    }

    /// <summary>Place a decoded LF-group region's LF-DC + block grid into the full-image arrays.</summary>
    private static void PlaceLfGroup(JxlLfGroup lg, int l, int lfgpr, int bw, int[][] fullLfMod, JxlBlockInfo[] fullBlockGrid)
    {
        int lcol = l % lfgpr, lrow = l / lfgpr;
        int lx0 = lcol * LfGroupBlocks, ly0 = lrow * LfGroupBlocks;
        for (int y = 0; y < lg.Bh; y++)
            for (int x = 0; x < lg.Bw; x++)
            {
                int fi = (ly0 + y) * bw + lx0 + x;
                fullBlockGrid[fi] = lg.BlockGrid[y * lg.Bw + x];
                for (int mc = 0; mc < 3; mc++)
                    fullLfMod[mc][fi] = lg.LfQuant[mc][y * lg.Bw + x];
            }
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
