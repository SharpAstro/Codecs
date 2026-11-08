namespace SharpAstro.Heif.Hevc;

/// <summary>
/// The coefficient scan orders of H.265 6.5.3-6.5.5: up-right diagonal (scanIdx 0), horizontal (1) and
/// vertical (2), for square blocks of 1, 2, 4 and 8 positions a side (log2 0..3). A residual uses the
/// 4x4 order inside each sub-block and the same kind of order over the sub-blocks; the scaling lists use
/// the 8x8 diagonal order.
/// </summary>
internal static class HevcScan
{
    /// <summary><c>Orders[log2BlockSize][scanIdx]</c>: for each scan position, its (x, y) packed as <c>x | y &lt;&lt; 8</c>.</summary>
    private static readonly ushort[][][] Orders = Build();

    /// <summary>One scan order: for each scan position, its (x, y) packed as <c>x | y &lt;&lt; 8</c>.</summary>
    public static ushort[] Order(int log2BlockSize, int scanIdx) => Orders[log2BlockSize][scanIdx];

    public static int X(int log2BlockSize, int scanIdx, int position) => Orders[log2BlockSize][scanIdx][position] & 0xFF;

    public static int Y(int log2BlockSize, int scanIdx, int position) => Orders[log2BlockSize][scanIdx][position] >> 8;

    private static ushort[][][] Build()
    {
        var orders = new ushort[4][][];
        for (var log2 = 0; log2 < 4; log2++)
            orders[log2] = [Diagonal(1 << log2), Horizontal(1 << log2), Vertical(1 << log2)];
        return orders;
    }

    /// <summary>6.5.3: walk each anti-diagonal from bottom-left to top-right, keeping the positions inside the block.</summary>
    internal static ushort[] Diagonal(int size)
    {
        var order = new ushort[size * size];
        var i = 0;
        int x = 0, y = 0;
        while (i < order.Length)
        {
            while (y >= 0)
            {
                if (x < size && y < size) order[i++] = (ushort)(x | y << 8);
                y--;
                x++;
            }
            y = x;
            x = 0;
        }
        return order;
    }

    /// <summary>6.5.4: row by row.</summary>
    internal static ushort[] Horizontal(int size)
    {
        var order = new ushort[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                order[y * size + x] = (ushort)(x | y << 8);
        return order;
    }

    /// <summary>6.5.5: column by column.</summary>
    internal static ushort[] Vertical(int size)
    {
        var order = new ushort[size * size];
        for (var x = 0; x < size; x++)
            for (var y = 0; y < size; y++)
                order[x * size + y] = (ushort)(x | y << 8);
        return order;
    }
}
