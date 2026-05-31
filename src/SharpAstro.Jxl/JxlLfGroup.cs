namespace SharpAstro.Jxl;

/// <summary>
/// A VarDCT <c>LfGroup</c> section (ISO/IEC 18181-1 §J, jxl-frame lf_group.rs): for one LF group it
/// carries the quantized LF-DC image (<c>LfCoeff</c>) and the per-varblock <c>HfMetadata</c>, both as
/// Modular sub-images sharing the frame's global MA tree (<see cref="JxlModularSubimage"/>).
///
/// <para>The section bit layout, in order, is:</para>
/// <list type="number">
///   <item><c>LfCoeff</c>: <c>extra_precision</c> (2 bits) then a 3-channel Modular sub-image
///         (stream <c>1 + lf_group_idx</c>) of the quantized DC, one sample per 8×8 block, in
///         modular channel order Y, X, B.</item>
///   <item><c>HfMetadata</c>: <c>nb_blocks − 1</c> in <c>ceil_log2(bw·bh)</c> bits then a 4-channel
///         Modular sub-image (stream <c>1 + 2·num_lf_groups + lf_group_idx</c>): <c>x_from_y</c> and
///         <c>b_from_y</c> (chroma-from-luma, <c>lf/64</c>), <c>block_info_raw</c> (<c>nb_blocks × 2</c>
///         — row 0 dct_select, row 1 hf_mul−1, in greedy placement order), and <c>sharpness</c>
///         (<c>bw × bh</c>, EPF). The block grid is reconstructed with <see cref="JxlBlockLayout"/>.</item>
/// </list>
///
/// <para>
/// Targets the minimal-encoder shape (all blocks the same transform is not required — any tiling
/// JxlBlockLayout accepts works — but chroma is full-resolution and EPF/sharpness is zero). The
/// sample data is split out of the shared tree's config exactly as in <see cref="JxlModularSubimage"/>,
/// so the caller prepares one entropy plan over every Modular sub-image stream in the frame.
/// </para>
/// </summary>
internal sealed class JxlLfGroup
{
    public required int Bw { get; init; }
    public required int Bh { get; init; }
    public required int ExtraPrecision { get; init; }

    /// <summary>Quantized LF-DC, modular channel order [Y, X, B], each <c>bw·bh</c> row-major.</summary>
    public required int[][] LfQuant { get; init; }

    public required JxlBlockInfo[] BlockGrid { get; init; }

    public required int CfW { get; init; }
    public required int CfH { get; init; }
    public required int[] XFromY { get; init; }
    public required int[] BFromY { get; init; }

    private JxlModularChannel[] BuildLfQuantChannels()
    {
        var chans = new JxlModularChannel[3];
        for (int c = 0; c < 3; c++)
        {
            var ch = new JxlModularChannel(Bw, Bh);
            Array.Copy(LfQuant[c], ch.Data, Bw * Bh);
            chans[c] = ch;
        }
        return chans;
    }

    private JxlModularChannel[] BuildHfMetaChannels()
    {
        (int[] dctSelect, int[] mul) = JxlBlockLayout.Encode(BlockGrid, Bw, Bh);
        int nb = dctSelect.Length;

        var xFromY = new JxlModularChannel(CfW, CfH);
        Array.Copy(XFromY, xFromY.Data, CfW * CfH);
        var bFromY = new JxlModularChannel(CfW, CfH);
        Array.Copy(BFromY, bFromY.Data, CfW * CfH);

        var blockInfoRaw = new JxlModularChannel(nb, 2); // row 0 dct_select, row 1 hf_mul-1
        for (int i = 0; i < nb; i++)
        {
            blockInfoRaw.Data[i] = dctSelect[i];
            blockInfoRaw.Data[nb + i] = mul[i];
        }

        var sharpness = new JxlModularChannel(Bw, Bh); // zero (EPF disabled)
        return [xFromY, bFromY, blockInfoRaw, sharpness];
    }

    /// <summary>The two Modular sample streams (LfCoeff, HfMetadata) for entropy plan preparation.</summary>
    public (List<(int Ctx, uint Value)> Lf, List<(int Ctx, uint Value)> Hf) BuildStreams()
        => (JxlModularSubimage.BuildSampleStream(BuildLfQuantChannels()),
            JxlModularSubimage.BuildSampleStream(BuildHfMetaChannels()));

    /// <summary>Write the LfGroup section, given the shared sample encoder/plan and this group's streams.</summary>
    public void Write(
        JxlBitWriter bw, JxlEntropyEncoder sampleEnc, JxlEntropyEncoder.Plan plan,
        List<(int Ctx, uint Value)> lfStream, List<(int Ctx, uint Value)> hfStream)
    {
        // LfCoeff: extra_precision + lf_quant sample data.
        bw.WriteBits((uint)ExtraPrecision, 2);
        sampleEnc.WriteData(bw, plan, lfStream);

        // HfMetadata: nb_blocks - 1 then hf_meta sample data.
        int nb = JxlBlockLayout.Encode(BlockGrid, Bw, Bh).DctSelect.Length;
        int nbBits = JxlHfGlobal.CeilLog2(Bw * Bh);
        if (nbBits > 0)
            bw.WriteBits((uint)(nb - 1), nbBits);
        sampleEnc.WriteData(bw, plan, hfStream);
    }

    /// <summary>Read an LfGroup section using the shared global tree and the two sub-image stream ids.</summary>
    public static JxlLfGroup Read(
        ref JxlBitReader br, JxlMaConfig tree, int bw, int bh, int cfW, int cfH,
        uint lfStreamIndex, uint hfStreamIndex)
    {
        // LfCoeff.
        int extraPrecision = (int)br.ReadBits(2);
        var lfChannels = new[]
        {
            new JxlModularChannel(bw, bh), new JxlModularChannel(bw, bh), new JxlModularChannel(bw, bh),
        };
        JxlModularImage.Decode(ref br, tree, lfChannels, JxlWpHeader.Default, lfStreamIndex);

        // HfMetadata.
        int nbBits = JxlHfGlobal.CeilLog2(bw * bh);
        int nb = (int)(nbBits == 0 ? 0u : br.ReadBits(nbBits)) + 1;
        var hfChannels = new[]
        {
            new JxlModularChannel(cfW, cfH), new JxlModularChannel(cfW, cfH),
            new JxlModularChannel(nb, 2), new JxlModularChannel(bw, bh),
        };
        JxlModularImage.Decode(ref br, tree, hfChannels, JxlWpHeader.Default, hfStreamIndex);

        int[] raw = hfChannels[2].Data;
        var dctSelect = new int[nb];
        var mul = new int[nb];
        Array.Copy(raw, 0, dctSelect, 0, nb);  // row 0
        Array.Copy(raw, nb, mul, 0, nb);       // row 1
        JxlBlockInfo[] grid = JxlBlockLayout.Decode(dctSelect, mul, bw, bh);

        return new JxlLfGroup
        {
            Bw = bw,
            Bh = bh,
            ExtraPrecision = extraPrecision,
            LfQuant = [lfChannels[0].Data, lfChannels[1].Data, lfChannels[2].Data],
            BlockGrid = grid,
            CfW = cfW,
            CfH = cfH,
            XFromY = hfChannels[0].Data,
            BFromY = hfChannels[1].Data,
        };
    }
}
