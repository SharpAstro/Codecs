namespace SharpAstro.Jxl;

/// <summary>
/// The VarDCT-specific part of LfGlobal (ISO/IEC 18181-1 §K.2): the global <c>Quantizer</c>
/// (global_scale, quant_lf), the <c>HfBlockContext</c> (entropy-context clustering for HF
/// coefficients), and the <c>LfChannelCorrelation</c> (chroma-from-luma base factors). Read/write
/// ported from jxl-oxide's <c>LfGlobalVarDct</c> + the <c>Quantizer</c> / <c>HfBlockContext</c> /
/// <c>LfChannelCorrelation</c> bundles in jxl-vardct/src/lf.rs.
///
/// <para>
/// The custom (non-default) <c>HfBlockContext</c> and <c>LfChannelCorrelation</c> forms are decode
/// paths needing the entropy cluster-map reader; this type currently reads/writes the
/// <c>all_default</c> forms (what a minimal VarDCT encoder emits) and rejects the custom forms on
/// read until the full decoder lands.
/// </para>
/// </summary>
internal sealed class JxlLfGlobalVarDct
{
    /// <summary>The fixed default HfBlockContext block_ctx_map (39 entries), num_block_clusters = 15.</summary>
    public static readonly byte[] DefaultBlockCtxMap =
    {
        0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14,
        14, 14, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
    };

    public required int GlobalScale { get; init; }
    public required int QuantLf { get; init; }

    public int NumBlockClusters { get; init; } = 15;
    public byte[] BlockCtxMap { get; init; } = DefaultBlockCtxMap;
    public int[] QfThresholds { get; init; } = [];
    public int[][] LfThresholds { get; init; } = [[], [], []];

    public int ColourFactor { get; init; } = JxlChromaFromLuma.DefaultColourFactor;
    public float BaseCorrelationX { get; init; } = JxlChromaFromLuma.DefaultBaseCorrelationX;
    public float BaseCorrelationB { get; init; } = JxlChromaFromLuma.DefaultBaseCorrelationB;
    public int XFactorLf { get; init; } = JxlChromaFromLuma.DefaultFactorLf;
    public int BFactorLf { get; init; } = JxlChromaFromLuma.DefaultFactorLf;

    public static JxlLfGlobalVarDct Read(ref JxlBitReader br)
    {
        // Quantizer.
        int globalScale = (int)br.ReadU32((1, 11), (2049, 11), (4097, 12), (8193, 16));
        int quantLf = (int)br.ReadU32((16, 0), (1, 5), (1, 8), (1, 16));

        // HfBlockContext.
        if (!br.ReadBit())
            throw new NotSupportedException("JPEG XL: custom HfBlockContext is not yet supported.");

        // LfChannelCorrelation.
        if (!br.ReadBit())
            throw new NotSupportedException("JPEG XL: custom LfChannelCorrelation is not yet supported.");

        return new JxlLfGlobalVarDct { GlobalScale = globalScale, QuantLf = quantLf };
    }

    public void Write(JxlBitWriter bw)
    {
        // Quantizer.
        bw.WriteU32((uint)GlobalScale, (1, 11), (2049, 11), (4097, 12), (8193, 16));
        bw.WriteU32((uint)QuantLf, (16, 0), (1, 5), (1, 8), (1, 16));

        // HfBlockContext all_default.
        if (NumBlockClusters != 15 || !ReferenceEquals(BlockCtxMap, DefaultBlockCtxMap))
            throw new NotSupportedException("JPEG XL: writing a custom HfBlockContext is not yet supported.");
        bw.WriteBit(true);

        // LfChannelCorrelation all_default.
        if (ColourFactor != JxlChromaFromLuma.DefaultColourFactor
            || BaseCorrelationX != JxlChromaFromLuma.DefaultBaseCorrelationX
            || BaseCorrelationB != JxlChromaFromLuma.DefaultBaseCorrelationB
            || XFactorLf != JxlChromaFromLuma.DefaultFactorLf
            || BFactorLf != JxlChromaFromLuma.DefaultFactorLf)
            throw new NotSupportedException("JPEG XL: writing a custom LfChannelCorrelation is not yet supported.");
        bw.WriteBit(true);
    }
}
