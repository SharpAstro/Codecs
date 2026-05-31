namespace SharpAstro.Jxl;

/// <summary>One cell of the VarDCT block grid (jxl-oxide <c>BlockInfo</c>).</summary>
internal readonly struct JxlBlockInfo
{
    public enum BlockState { Uninit, Occupied, Data }

    public BlockState State { get; private init; }
    public JxlVarDctTransform DctSelect { get; private init; }
    public int HfMul { get; private init; }

    /// <summary>True for both the top-left <see cref="BlockState.Data"/> cell and its covered cells.</summary>
    public bool IsOccupied => State != BlockState.Uninit;

    public static JxlBlockInfo Covered => new() { State = BlockState.Occupied };

    public static JxlBlockInfo Data(JxlVarDctTransform dctSelect, int hfMul)
        => new() { State = BlockState.Data, DctSelect = dctSelect, HfMul = hfMul };
}

/// <summary>
/// The VarDCT varblock layout within an LF group (ISO/IEC 18181-1 §J.2): the HfMetadata's
/// <c>block_info_raw</c> channel lists (dct_select, hf_mul−1) pairs that are placed greedily over the
/// <c>bw×bh</c> 8×8-block grid — at each still-free cell in raster order, the next pair drops a
/// varblock of that transform's size, its top-left marked Data and the rest Occupied. Ported from
/// jxl-oxide's <c>HfMetadata::parse</c> block-placement loop; the encoder is the exact inverse.
/// </summary>
internal static class JxlBlockLayout
{
    /// <summary>Place the raw (dct_select, hf_mul−1) pairs onto a <paramref name="bw"/>×<paramref name="bh"/> grid.</summary>
    public static JxlBlockInfo[] Decode(IReadOnlyList<int> dctSelectRaw, IReadOnlyList<int> mulRaw, int bw, int bh)
    {
        var grid = new JxlBlockInfo[bw * bh]; // default = Uninit
        int dataIdx = 0;

        for (int y = 0; y < bh; y++)
        {
            int x = 0;
            while (x < bw)
            {
                if (grid[y * bw + x].IsOccupied)
                {
                    x++;
                    continue;
                }

                var dctSelect = (JxlVarDctTransform)dctSelectRaw[dataIdx];
                int hfMul = mulRaw[dataIdx] + 1;
                if (hfMul <= 0)
                    throw new InvalidDataException("JPEG XL: non-positive HfMul.");

                (int dw, int dh) = dctSelect.DctSelectSize();
                if (x % 32 + dw > 32 || y % 32 + dh > 32)
                    throw new InvalidDataException("JPEG XL: varblock crosses a pass-group border.");

                for (int dy = 0; dy < dh; dy++)
                    for (int dx = 0; dx < dw; dx++)
                    {
                        int cx = x + dx, cy = y + dy;
                        if (cx >= bw || cy >= bh)
                            throw new InvalidDataException("JPEG XL: varblock doesn't fit in the LF group.");
                        int idx = cy * bw + cx;
                        if (grid[idx].IsOccupied)
                            throw new InvalidDataException("JPEG XL: varblocks overlap.");
                        grid[idx] = (dx == 0 && dy == 0) ? JxlBlockInfo.Data(dctSelect, hfMul) : JxlBlockInfo.Covered;
                    }

                dataIdx++;
                x += dw;
            }
        }
        return grid;
    }

    /// <summary>Serialise a tiled grid back to raw (dct_select, hf_mul−1) pairs, in placement order.</summary>
    public static (int[] DctSelect, int[] Mul) Encode(JxlBlockInfo[] grid, int bw, int bh)
    {
        var dctSelect = new List<int>();
        var mul = new List<int>();
        var consumed = new bool[bw * bh];

        for (int y = 0; y < bh; y++)
        {
            int x = 0;
            while (x < bw)
            {
                if (consumed[y * bw + x])
                {
                    x++;
                    continue;
                }

                JxlBlockInfo info = grid[y * bw + x];
                if (info.State != JxlBlockInfo.BlockState.Data)
                    throw new InvalidOperationException("JPEG XL: block grid is not a valid tiling (expected a Data block at the next free cell).");

                dctSelect.Add((int)info.DctSelect);
                mul.Add(info.HfMul - 1);

                (int dw, int dh) = info.DctSelect.DctSelectSize();
                for (int dy = 0; dy < dh; dy++)
                    for (int dx = 0; dx < dw; dx++)
                        consumed[(y + dy) * bw + (x + dx)] = true;
                x += dw;
            }
        }
        return (dctSelect.ToArray(), mul.ToArray());
    }
}
