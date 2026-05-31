using System.Numerics;

namespace SharpAstro.Jxl;

/// <summary>
/// The VarDCT HF-coefficient entropy codec (ISO/IEC 18181-1 §K.4.5) — the PassGroup body. A faithful
/// port of jxl-oxide's <c>write_hf_coeff</c> (jxl-vardct/src/hf_coeff.rs); <see cref="Decode"/> mirrors
/// it line-for-line and <see cref="Encode"/> is its exact inverse.
///
/// <para>
/// Per varblock, per channel (processed in the order Y, X, B), it codes a <c>non_zeros</c> count under
/// a context predicted from the left/top neighbours' counts, then the AC coefficients in scan order
/// (<see cref="JxlCoeffOrder.NaturalOrder"/> from <c>order[num_blocks..]</c>) under a context derived
/// from the <em>remaining</em> non-zero count and the scan position. The running <c>non_zeros</c>
/// decrements on every coded coefficient and the scan stops once it reaches zero, which is what keeps
/// the coefficient-context sum within the 458-wide per-block-context band.
/// </para>
///
/// <para>
/// This implementation targets the minimal-encoder shape: no chroma subsampling
/// (<c>jpeg_upsampling = [0,0,0]</c>, so all per-channel shifts are zero) and the default block
/// context (so <c>lf_idx</c>/<c>hf_idx</c> collapse to 0 when the threshold lists are empty). The
/// caller owns the surrounding bitstream: the <c>hfp</c> preset bits and the entropy data section
/// (initial rANS state via <see cref="JxlEntropyDecoder.Begin"/>) bracket <see cref="Decode"/>.
/// </para>
/// </summary>
internal static class JxlHfCoeff
{
    // Coefficient frequency context, indexed by (scan_index >> num_blocks_log). hf_coeff.rs:26.
    private static readonly uint[] CoeffFreqContext =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19,
        20, 20, 21, 21, 22, 22, 23, 23, 23, 23, 24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26, 27,
        27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
    ];

    // Remaining-non-zero context, indexed by ((non_zeros - 1) >> num_blocks_log). hf_coeff.rs:31.
    private static readonly uint[] CoeffNumNonzeroContext =
    [
        0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123, 152, 152, 152, 152, 152, 152, 152, 152,
        180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206,
    ];

    /// <summary>Outer-loop channel index → physical XYB channel: Y(1), X(0), B(2). hf_coeff.rs:142.</summary>
    private static readonly int[] ChannelRemap = [1, 0, 2];

    /// <summary>
    /// Inputs shared by <see cref="Encode"/> and <see cref="Decode"/>. The coefficient grids are the
    /// per-channel full-image planes in physical XYB order (<c>[0]=X, [1]=Y, [2]=B</c>), each of size
    /// <c>(Bw*8) × (Bh*8)</c> stored row-major; <see cref="Decode"/> adds the decoded coefficients into
    /// them (LLF/DC positions are written by the LF path, not here).
    /// </summary>
    internal sealed class Params
    {
        public required int Bw { get; init; }
        public required int Bh { get; init; }
        public required JxlBlockInfo[] BlockGrid { get; init; }
        public required int NumBlockClusters { get; init; }
        public required byte[] BlockCtxMap { get; init; }
        public int[] QfThresholds { get; init; } = [];
        public int[][] LfThresholds { get; init; } = [[], [], []];

        /// <summary>Quantized LF DC per physical channel (only consulted when a threshold list is non-empty).</summary>
        public int[][]? LfQuant { get; init; }
        public int CoeffShift { get; init; }

        internal int LfIdxMul => (LfThresholds[0].Length + 1) * (LfThresholds[1].Length + 1) * (LfThresholds[2].Length + 1);
        internal int HfIdxMul => QfThresholds.Length + 1;
    }

    /// <summary>Encode the HF coefficients of every Data block into the entropy symbol stream.</summary>
    public static List<(int Ctx, uint Value)> Encode(Params p, int[][] coeffGrid)
    {
        var stream = new List<(int Ctx, uint Value)>();
        int gridW = p.Bw * 8;
        var nz = new uint[3][];
        for (int c = 0; c < 3; c++)
            nz[c] = new uint[p.Bw];

        for (int y = 0; y < p.Bh; y++)
            for (int x = 0; x < p.Bw; x++)
            {
                JxlBlockInfo info = p.BlockGrid[y * p.Bw + x];
                if (info.State != JxlBlockInfo.BlockState.Data)
                    continue;

                (int w8, int h8) = info.DctSelect.DctSelectSize();
                int numBlocks = w8 * h8;
                int numBlocksLog = BitOperations.TrailingZeroCount((uint)numBlocks);
                int orderId = info.DctSelect.OrderId();
                int lfIdx = ComputeLfIdx(p, x, y);
                int hfIdx = ComputeHfIdx(p, info.HfMul);

                for (int outerC = 0; outerC < 3; outerC++)
                {
                    int chIdx = outerC * 13 + orderId;
                    int c = ChannelRemap[outerC];
                    int sx = x, sy = y; // no subsampling

                    int blockCtx = p.BlockCtxMap[(chIdx * p.HfIdxMul + hfIdx) * p.LfIdxMul + lfIdx];
                    int nonZerosCtx = NonZerosContext(blockCtx, p.NumBlockClusters, nz[c], sx, sy);

                    (int X, int Y)[] order = JxlCoeffOrder.NaturalOrder(orderId);
                    int hfStart = numBlocks;

                    // Count the non-zero HF coefficients for this block/channel.
                    uint nonZeros = 0;
                    for (int i = hfStart; i < order.Length; i++)
                        if (CoeffAt(coeffGrid[c], gridW, info.DctSelect, sx, sy, order[i]) != 0)
                            nonZeros++;

                    stream.Add((nonZerosCtx, nonZeros));

                    uint nonZerosVal = (nonZeros + (uint)numBlocks - 1) >> numBlocksLog;
                    for (int dx = 0; dx < w8; dx++)
                        nz[c][sx + dx] = nonZerosVal;
                    if (nonZeros == 0)
                        continue;

                    uint isPrevNonzero = nonZeros <= (uint)numBlocks * 4 ? 1u : 0u;
                    int coeffCtxBase = blockCtx * 458 + 37 * p.NumBlockClusters;
                    uint remaining = nonZeros;

                    for (int i = hfStart; i < order.Length; i++)
                    {
                        int coeffCtx = CoeffContext(remaining, i - hfStart, numBlocksLog, isPrevNonzero);
                        int coeff = CoeffAt(coeffGrid[c], gridW, info.DctSelect, sx, sy, order[i]);
                        uint ucoeff = JxlModular.PackSigned(coeff >> p.CoeffShift);
                        stream.Add((coeffCtxBase + coeffCtx, ucoeff));

                        if (ucoeff == 0)
                        {
                            isPrevNonzero = 0;
                            continue;
                        }
                        isPrevNonzero = 1;
                        remaining--;
                        if (remaining == 0)
                            break;
                    }
                }
            }

        return stream;
    }

    /// <summary>
    /// Decode the HF coefficients into <paramref name="coeffGrid"/>, reading the preset bits and then
    /// the entropy data. Mirrors <c>write_hf_coeff</c>; <paramref name="dist"/> must already have had
    /// its config parsed (HfGlobal), and <paramref name="clusterMap"/> is its full 495·num_block_clusters·
    /// num_hf_presets context→cluster map (the preset slice is taken here).
    /// </summary>
    public static void Decode(ref JxlBitReader br, JxlEntropyDecoder dist, byte[] clusterMap, int numHfPresets, Params p, int[][] coeffGrid)
    {
        int hfpBits = BitOperations.TrailingZeroCount(BitOperations.RoundUpToPowerOf2((uint)numHfPresets));
        uint hfp = hfpBits == 0 ? 0 : br.ReadBits(hfpBits);
        if (hfp >= numHfPresets)
            throw new InvalidDataException("JPEG XL: selected HF preset out of bounds.");

        int ctxSize = 495 * p.NumBlockClusters;
        int baseOff = ctxSize * (int)hfp;

        dist.Begin(ref br);

        int gridW = p.Bw * 8;
        var nz = new uint[3][];
        for (int c = 0; c < 3; c++)
            nz[c] = new uint[p.Bw];

        for (int y = 0; y < p.Bh; y++)
            for (int x = 0; x < p.Bw; x++)
            {
                JxlBlockInfo info = p.BlockGrid[y * p.Bw + x];
                if (info.State != JxlBlockInfo.BlockState.Data)
                    continue;

                (int w8, int h8) = info.DctSelect.DctSelectSize();
                int numBlocks = w8 * h8;
                int numBlocksLog = BitOperations.TrailingZeroCount((uint)numBlocks);
                int orderId = info.DctSelect.OrderId();
                int lfIdx = ComputeLfIdx(p, x, y);
                int hfIdx = ComputeHfIdx(p, info.HfMul);

                for (int outerC = 0; outerC < 3; outerC++)
                {
                    int chIdx = outerC * 13 + orderId;
                    int c = ChannelRemap[outerC];
                    int sx = x, sy = y; // no subsampling

                    int blockCtx = p.BlockCtxMap[(chIdx * p.HfIdxMul + hfIdx) * p.LfIdxMul + lfIdx];
                    int nonZerosCtx = NonZerosContext(blockCtx, p.NumBlockClusters, nz[c], sx, sy);

                    uint nonZeros = dist.ReadVarintClustered(ref br, clusterMap[baseOff + nonZerosCtx], 0);
                    if (nonZeros > (63u << numBlocksLog))
                        throw new InvalidDataException("JPEG XL: non_zeros too large.");

                    uint nonZerosVal = (nonZeros + (uint)numBlocks - 1) >> numBlocksLog;
                    for (int dx = 0; dx < w8; dx++)
                        nz[c][sx + dx] = nonZerosVal;
                    if (nonZeros == 0)
                        continue;

                    uint isPrevNonzero = nonZeros <= (uint)numBlocks * 4 ? 1u : 0u;
                    (int X, int Y)[] order = JxlCoeffOrder.NaturalOrder(orderId);
                    int hfStart = numBlocks;
                    int coeffCtxBase = blockCtx * 458 + 37 * p.NumBlockClusters;

                    for (int i = hfStart; i < order.Length; i++)
                    {
                        int coeffCtx = CoeffContext(nonZeros, i - hfStart, numBlocksLog, isPrevNonzero);
                        if (coeffCtx >= 458)
                            throw new InvalidDataException("JPEG XL: too many zeros in varblock HF coefficient.");
                        uint ucoeff = dist.ReadVarintClustered(ref br, clusterMap[baseOff + coeffCtxBase + coeffCtx], 0);
                        if (ucoeff == 0)
                        {
                            isPrevNonzero = 0;
                            continue;
                        }

                        int coeff = JxlModular.UnpackSigned(ucoeff) << p.CoeffShift;
                        WriteCoeffAt(coeffGrid[c], gridW, info.DctSelect, sx, sy, order[i], coeff);

                        isPrevNonzero = 1;
                        nonZeros--;
                        if (nonZeros == 0)
                            break;
                    }
                }
            }

        dist.Finish();
    }

    /// <summary>The non-zero count context: block context plus a bucket of the neighbour-predicted count.</summary>
    private static int NonZerosContext(int blockCtx, int numBlockClusters, uint[] nzRow, int sx, int sy)
    {
        uint predicted = sy == 0
            ? (sx == 0 ? 32u : nzRow[sx - 1])
            : sx == 0 ? nzRow[sx] : (nzRow[sx] + nzRow[sx - 1] + 1) >> 1;
        uint idx = predicted >= 8 ? 4 + predicted / 2 : predicted;
        return blockCtx + (int)idx * numBlockClusters;
    }

    /// <summary>The per-coefficient context: (num-nonzero bucket + freq bucket)·2 + is_prev_nonzero.</summary>
    private static int CoeffContext(uint remainingNonZeros, int scanIdx, int numBlocksLog, uint isPrevNonzero)
    {
        uint nzBucket = (remainingNonZeros - 1) >> numBlocksLog;
        int freqIdx = scanIdx >> numBlocksLog;
        return (int)((CoeffNumNonzeroContext[nzBucket] + CoeffFreqContext[freqIdx]) * 2 + isPrevNonzero);
    }

    private static int ComputeLfIdx(Params p, int x, int y)
    {
        if (p.LfThresholds[0].Length == 0 && p.LfThresholds[1].Length == 0 && p.LfThresholds[2].Length == 0)
            return 0;
        if (p.LfQuant is null)
            throw new InvalidOperationException("JPEG XL: lf_thresholds require lf_quant.");

        int idx = 0;
        foreach (int cc in (ReadOnlySpan<int>)[0, 2, 1])
        {
            int[] thr = p.LfThresholds[cc];
            idx *= thr.Length + 1;
            int q = p.LfQuant[cc][y * p.Bw + x]; // no subsampling
            foreach (int t in thr)
                if (q > t)
                    idx++;
        }
        return idx;
    }

    private static int ComputeHfIdx(Params p, int qf)
    {
        int idx = 0;
        foreach (int t in p.QfThresholds)
            if (qf > t)
                idx++;
        return idx;
    }

    /// <summary>Read a coefficient from a channel plane at the given block cell + scan coordinate.</summary>
    private static int CoeffAt(int[] plane, int gridW, JxlVarDctTransform dctSelect, int sx, int sy, (int X, int Y) coord)
    {
        (int gx, int gy) = GridCoord(dctSelect, sx, sy, coord);
        return plane[gy * gridW + gx];
    }

    private static void WriteCoeffAt(int[] plane, int gridW, JxlVarDctTransform dctSelect, int sx, int sy, (int X, int Y) coord, int coeff)
    {
        (int gx, int gy) = GridCoord(dctSelect, sx, sy, coord);
        plane[gy * gridW + gx] += coeff;
    }

    private static (int X, int Y) GridCoord(JxlVarDctTransform dctSelect, int sx, int sy, (int X, int Y) coord)
    {
        int dx = coord.X, dy = coord.Y;
        if (dctSelect.NeedTranspose())
            (dx, dy) = (dy, dx);
        return (sx * 8 + dx, sy * 8 + dy);
    }
}
