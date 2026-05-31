using System.Numerics;

namespace SharpAstro.Jxl;

/// <summary>
/// The VarDCT <c>HfGlobal</c> section (ISO/IEC 18181-1 §L): the per-frame HF configuration shared by
/// every PassGroup. Ported from jxl-oxide's <c>HfGlobal</c> (jxl-frame/data/hf_global.rs) +
/// <c>HfPass</c> (jxl-vardct/hf_pass.rs). It carries, in order:
/// <list type="number">
///   <item>the <c>DequantMatrixSet</c> — an <c>all_default</c> bit selecting <see cref="JxlDequantMatrices.BuildDefault"/>;</item>
///   <item><c>num_hf_presets</c> = <c>read_bits(ceil_log2(num_groups)) + 1</c>;</item>
///   <item>per pass, an <c>HfPass</c>: the <c>used_orders</c> selector (0 ⇒ all-natural scan) and then
///         the <c>hf_dist</c> entropy <em>config</em> for the HF-coefficient symbols.</item>
/// </list>
///
/// <para>
/// The <c>hf_dist</c> entropy config lives here, but the coded symbols live in each PassGroup — the
/// config/data split handled by <see cref="JxlEntropyEncoder"/> (5i). On encode the caller builds the
/// shared <see cref="JxlEntropyEncoder.Plan"/> from the PassGroup streams and passes it to
/// <see cref="Write"/>; on decode <see cref="Read"/> returns the parsed <see cref="HfDist"/> decoder
/// (config only), which <see cref="JxlHfCoeff.Decode"/> then drives over the PassGroup data.
/// </para>
///
/// <para>
/// This targets the minimal-encoder shape: default dequant matrices and all-natural coefficient
/// orders (<c>used_orders == 0</c>). Custom matrices and per-pass permutations are decode paths still
/// to port; both are rejected on read.
/// </para>
/// </summary>
internal sealed class JxlHfGlobal
{
    public required int NumHfPresets { get; init; }
    public required JxlDequantMatrices DequantMatrices { get; init; }

    /// <summary>The hf_dist entropy decoder with its config already parsed; data is read later in the PassGroup.</summary>
    public required JxlEntropyDecoder HfDist { get; init; }

    /// <summary><c>ceil(log2(n)) = next_power_of_two(n).trailing_zeros()</c>; the bit width of <c>num_hf_presets − 1</c>.</summary>
    internal static int CeilLog2(int n) => BitOperations.TrailingZeroCount(BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n)));

    public static JxlHfGlobal Read(ref JxlBitReader br, int numGroups, int numBlockClusters)
    {
        // DequantMatrixSet.
        if (!br.ReadBit())
            throw new NotSupportedException("JPEG XL: custom DequantMatrixSet is not yet supported.");
        JxlDequantMatrices dequant = JxlDequantMatrices.BuildDefault();

        // num_hf_presets = read_bits(ceil_log2(num_groups)) + 1.
        int presetBits = CeilLog2(numGroups);
        int numHfPresets = (int)(presetBits == 0 ? 0u : br.ReadBits(presetBits)) + 1;

        // HfPass (num_passes == 1).
        uint usedOrders = br.ReadU32((0x5f, 0), (0x13, 0), (0, 0), (0, 13));
        if (usedOrders != 0)
            throw new NotSupportedException("JPEG XL: custom coefficient orders are not yet supported.");

        JxlEntropyDecoder hfDist = JxlEntropyDecoder.Parse(
            ref br, (uint)(495 * numHfPresets * numBlockClusters));

        return new JxlHfGlobal { NumHfPresets = numHfPresets, DequantMatrices = dequant, HfDist = hfDist };
    }

    /// <summary>
    /// Write the HfGlobal section: the default-matrix bit, <c>num_hf_presets</c>, the all-natural
    /// <c>used_orders</c>, then the hf_dist entropy config from <paramref name="hfDistPlan"/>.
    /// </summary>
    public static void Write(
        JxlBitWriter bw, int numGroups, int numHfPresets,
        JxlEntropyEncoder hfDistEnc, JxlEntropyEncoder.Plan hfDistPlan)
    {
        // DequantMatrixSet all_default.
        bw.WriteBit(true);

        // num_hf_presets (only the bits ceil_log2(num_groups) wide; nothing for a single group).
        int presetBits = CeilLog2(numGroups);
        if (presetBits > 0)
            bw.WriteBits((uint)(numHfPresets - 1), presetBits);

        // HfPass: used_orders = 0 -> all-natural scan (WriteU32 picks the offset-0 / 0-bit option).
        bw.WriteU32(0, (0x5f, 0), (0x13, 0), (0, 0), (0, 13));

        // hf_dist entropy config (symbols follow in the PassGroup, written via WriteData).
        hfDistEnc.WriteConfig(bw, hfDistPlan);
    }
}
