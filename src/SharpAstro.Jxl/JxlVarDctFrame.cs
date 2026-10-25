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

        // Only the LF DC (one sample per block) and the output span the image. HF coefficients are decoded,
        // reconstructed and written out one PassGroup at a time through GroupReconstructor's buffers.
        var fullLfMod = new int[3][]; // modular channel order [Y, X, B], full bw×bh
        for (int c = 0; c < 3; c++)
            fullLfMod[c] = new int[bw * bh];
        var fullBlockGrid = new JxlBlockInfo[bw * bh];
        var rgb = new int[3][];
        for (int c = 0; c < 3; c++)
            rgb[c] = new int[width * height];

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
            var recon = new GroupReconstructor(lfVarDct, fullBlockGrid, fullLfMod, extraPrecision, bw, bh, width, rgb);
            DecodePassGroup(ref br, hfGlobal, recon, groupIdx: 0, gpr: 1);
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

            var recon = new GroupReconstructor(lfVarDct, fullBlockGrid, fullLfMod, extraPrecision, bw, bh, width, rgb);
            for (int g = 0; g < numGroups; g++)
            {
                int idx = hfgIdx + 1 + g;
                var pgBr = new JxlBitReader(cs.AsSpan(offsets[idx], (int)toc.Sizes[idx]));
                DecodePassGroup(ref pgBr, hfGlobal, recon, g, gpr);
            }
        }

        return rgb;
    }

    /// <summary>
    /// Everything a PassGroup needs to turn its HF coefficients into output pixels: the frame's LF DC and
    /// block grid (complete before any PassGroup is read), the transform settings, and the group-sized
    /// buffers that are reused from one group to the next.
    /// </summary>
    private sealed class GroupReconstructor
    {
        public GroupReconstructor(
            JxlLfGlobalVarDct lfVarDct, JxlBlockInfo[] blockGrid, int[][] lfMod, int extraPrecision,
            int bw, int bh, int width, int[][] rgb)
        {
            BlockGrid = blockGrid;
            Bw = bw;
            Bh = bh;
            Width = width;
            Rgb = rgb;
            // LfQuant is modular order [Y, X, B]; the transform wants physical [X, Y, B].
            LfPhys = [lfMod[1], lfMod[0], lfMod[2]];
            Settings = new JxlVarDctImage.Settings(
                lfVarDct.GlobalScale, lfVarDct.QuantLf, blockGrid[0].HfMul, extraPrecision);
            int groupPixels = Math.Min(GroupBlocks, bw) * 8 * (Math.Min(GroupBlocks, bh) * 8);
            Workspace = new JxlVarDctImage.Workspace(groupPixels);
            GroupHf = [new int[groupPixels], new int[groupPixels], new int[groupPixels]];
        }

        public JxlBlockInfo[] BlockGrid { get; }
        public int Bw { get; }
        public int Bh { get; }
        public int Width { get; }
        public int[][] Rgb { get; }
        public int[][] LfPhys { get; }
        public JxlVarDctImage.Settings Settings { get; }
        public JxlVarDctImage.Workspace Workspace { get; }
        public int[][] GroupHf { get; }

        /// <summary>Reconstruct the group at blocks (<paramref name="bx0"/>, <paramref name="by0"/>) from
        /// <see cref="GroupHf"/> and write its pixels into <see cref="Rgb"/>.</summary>
        public void Reconstruct(int bx0, int by0, int bwG, int bhG)
        {
            int subW = bwG * 8;
            var lf = new JxlVarDctImage.PlaneView[3];
            var hf = new JxlVarDctImage.PlaneView[3];
            for (int c = 0; c < 3; c++)
            {
                lf[c] = new(LfPhys[c], Bw, bx0, by0);
                hf[c] = new(GroupHf[c], subW, 0, 0);
            }
            JxlVarDctImage.ReconstructRegion(Workspace, bwG, bhG, Settings, lf, hf);

            float[][] srgb = Workspace.Coeff;
            for (int c = 0; c < 3; c++)
                for (int py = 0; py < bhG * 8; py++)
                {
                    int dst = (by0 * 8 + py) * Width + bx0 * 8;
                    int src = py * subW;
                    for (int px = 0; px < subW; px++)
                        Rgb[c][dst + px] = (int)MathF.Round(Math.Clamp(srgb[c][src + px], 0f, 1f) * 255f);
                }
        }
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

    /// <summary>
    /// Decode one PassGroup's HF coefficients (a ≤32×32-block region) and reconstruct its pixels straight
    /// into the output.
    /// </summary>
    private static void DecodePassGroup(
        ref JxlBitReader br, JxlHfGlobal hfGlobal, GroupReconstructor recon, int groupIdx, int gpr)
    {
        int bw = recon.Bw, bh = recon.Bh;
        int gcol = groupIdx % gpr, grow = groupIdx / gpr;
        int bx0 = gcol * GroupBlocks, by0 = grow * GroupBlocks;
        int bwG = Math.Min(GroupBlocks, bw - bx0);
        int bhG = Math.Min(GroupBlocks, bh - by0);

        var subGrid = new JxlBlockInfo[bwG * bhG];
        for (int y = 0; y < bhG; y++)
            for (int x = 0; x < bwG; x++)
                subGrid[y * bwG + x] = recon.BlockGrid[(by0 + y) * bw + (bx0 + x)];

        // JxlHfCoeff.Decode writes only up to each block's last nonzero coefficient, so the reused buffer
        // must start from the zeros a fresh allocation would have.
        int subPixels = bwG * 8 * (bhG * 8);
        for (int c = 0; c < 3; c++)
            Array.Clear(recon.GroupHf[c], 0, subPixels);

        var p = new JxlHfCoeff.Params
        {
            Bw = bwG, Bh = bhG, BlockGrid = subGrid,
            NumBlockClusters = 15, BlockCtxMap = JxlLfGlobalVarDct.DefaultBlockCtxMap, // our encoder's default HfBlockContext
        };
        JxlHfCoeff.Decode(ref br, hfGlobal.HfDist, hfGlobal.HfDist.ClusterMap, hfGlobal.NumHfPresets, p, recon.GroupHf);

        recon.Reconstruct(bx0, by0, bwG, bhG);
    }
}
