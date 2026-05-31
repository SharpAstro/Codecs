namespace SharpAstro.Jxl;

/// <summary>
/// Decodes a minimal single-group VarDCT JPEG XL frame (the inverse of <see cref="JxlVarDctEncoder"/>)
/// back to 8-bit RGB. It threads the same section readers the encoder's writers mirror — LfGlobal
/// (<see cref="JxlLfGlobalVarDct"/>) + GlobalModular tree (<see cref="JxlMaConfig"/>),
/// <see cref="JxlLfGroup"/>, <see cref="JxlHfGlobal"/>, and the PassGroup HF coefficients
/// (<see cref="JxlHfCoeff"/>) — then reconstructs pixels via <see cref="JxlVarDctImage"/>.
///
/// <para>
/// This is primarily a self-consistency check for the encoder: it does not yet apply
/// adaptive-LF-smoothing or the restoration filters, so it matches our own (filter-free) encode but is
/// not a general libjxl-compatible VarDCT decoder. Limited to the all-DCT8, single-group, xyb path.
/// </para>
/// </summary>
internal static class JxlVarDctFrame
{
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
        if (toc.EntryCount != 1)
            throw new NotSupportedException("JPEG XL: only single-group VarDCT frames are supported.");

        // LfGlobal.
        if ((frame.Flags & 0x02) != 0 || (frame.Flags & 0x10) != 0 || (frame.Flags & 0x01) != 0)
            throw new NotSupportedException("JPEG XL: patches/splines/noise are not supported.");
        if (!br.ReadBit()) // LfChannelDequantization all_default
            for (int i = 0; i < 3; i++) br.ReadBits(16);
        JxlLfGlobalVarDct lfVarDct = JxlLfGlobalVarDct.Read(ref br);

        // GlobalModular: a global tree, no colour channels (empty stream-0, no header).
        if (!br.ReadBit())
            throw new NotSupportedException("JPEG XL: VarDCT frame without a global tree is not supported.");
        int numChannels = 3 + meta.NumExtraChannels;
        int nodeLimit = (int)Math.Min(1 << 22, 1024 + (long)width * height * numChannels / 16);
        JxlMaConfig tree = JxlMaConfig.Parse(ref br, nodeLimit);

        int bw = (width + 7) / 8, bh = (height + 7) / 8;
        int cfW = (width + 63) / 64, cfH = (height + 63) / 64;

        // LfGroup (lf_group_idx = 0): lf_quant stream = 1, hf_meta stream = 1 + 2·num_lf_groups.
        JxlLfGroup lfGroup = JxlLfGroup.Read(
            ref br, tree, bw, bh, cfW, cfH,
            lfStreamIndex: 1, hfStreamIndex: (uint)(1 + 2 * frame.NumLfGroups));

        // HfGlobal.
        JxlHfGlobal hfGlobal = JxlHfGlobal.Read(ref br, numGroups: frame.NumGroups, numBlockClusters: lfVarDct.NumBlockClusters);

        // PassGroup: HF coefficients.
        var hfParams = new JxlHfCoeff.Params
        {
            Bw = bw, Bh = bh, BlockGrid = lfGroup.BlockGrid,
            NumBlockClusters = lfVarDct.NumBlockClusters, BlockCtxMap = lfVarDct.BlockCtxMap,
        };
        var hfGrid = new int[3][];
        for (int c = 0; c < 3; c++)
            hfGrid[c] = new int[width * height];
        JxlHfCoeff.Decode(ref br, hfGlobal.HfDist, hfGlobal.HfDist.ClusterMap, hfGlobal.NumHfPresets, hfParams, hfGrid);

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
}
