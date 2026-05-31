namespace SharpAstro.Jxl;

/// <summary>
/// The VarDCT coefficient "natural order" (ISO/IEC 18181-1 §K.3.4) — the default scan order in which
/// a block's HF coefficients are serialised, per order-id (0…12). A faithful port of jxl-oxide's
/// <c>fill_natural_order</c> (jxl-vardct/src/hf_pass.rs): the low-frequency LLF block is listed
/// row-major first, then the remaining positions are visited on a boustrophedon (alternating
/// diagonal) sweep, with the row coordinate folded by the block's aspect ratio.
///
/// <para>
/// The bitstream may override this with a per-pass permutation; this is the order that permutation
/// indexes into, and the order used when none is signalled.
/// </para>
/// </summary>
internal static class JxlCoeffOrder
{
    // (width, height) in samples, indexed by order-id (JxlVarDctTransformExtensions.OrderId).
    private static readonly (int W, int H)[] BlockSizes =
    {
        (8, 8), (8, 8), (16, 16), (32, 32), (16, 8), (32, 8), (32, 16),
        (64, 64), (64, 32), (128, 128), (128, 64), (256, 256), (256, 128),
    };

    private static readonly (int X, int Y)[]?[] Cache = new (int X, int Y)[]?[BlockSizes.Length];
    private static readonly object CacheLock = new();

    /// <summary>
    /// The natural coefficient order for an order-id: a length <c>w·h</c> list of (x, y) coefficient
    /// positions, in scan order. The result is a bijection onto the full <c>[0,w)×[0,h)</c> grid.
    /// </summary>
    public static (int X, int Y)[] NaturalOrder(int orderId)
    {
        lock (CacheLock)
        {
            if (Cache[orderId] is { } cached)
                return cached;

            (int bw, int bh) = BlockSizes[orderId];
            int yScale = bw / bh;
            int lbw = bw / 8;
            int lbh = bh / 8;

            var output = new (int X, int Y)[bw * bh];
            int idx = 0;

            // The LLF block is listed first, row-major.
            for (; idx < lbw * lbh; idx++)
                output[idx] = (idx % lbw, idx / lbw);

            // Then a boustrophedon diagonal sweep over the remaining positions.
            for (int dist = 1; dist < 2 * bw; dist++)
            {
                int margin = Math.Max(0, dist - bw);
                for (int order = margin; order < dist - margin; order++)
                {
                    int x, y;
                    if (dist % 2 == 1) { x = order; y = dist - 1 - order; }
                    else { x = dist - 1 - order; y = order; }

                    if (x < lbw && y < lbw)
                        continue; // already covered by the LLF block
                    if (y % yScale != 0)
                        continue; // not on this aspect-ratio's row grid

                    output[idx++] = (x, y / yScale);
                }
            }

            Cache[orderId] = output;
            return output;
        }
    }
}
