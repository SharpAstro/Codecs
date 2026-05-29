namespace SharpAstro.Jxr;

/// <summary>
/// DC coefficient prediction across the macroblock grid — T.832 §9.6.1.
/// </summary>
/// <remarks>
/// The DC prediction works on the super-DC value of each macroblock (one
/// scalar per colour component per MB). Four modes are defined:
/// <list type="bullet">
///   <item>0 — predict from left neighbour</item>
///   <item>1 — predict from top neighbour</item>
///   <item>2 — predict from average of left and top</item>
///   <item>3 — no prediction (used at the top-left MB of a tile)</item>
/// </list>
/// The direction is chosen deterministically from neighbour DC differences,
/// so both encoder and decoder agree on the mode without explicitly
/// signalling it. The encoder subtracts the prediction (forward direction);
/// the decoder adds it back (inverse). Tiles in JXR also have left/top
/// edges, so the caller passes an array of tile-edge flags.
/// </remarks>
public static class DcPrediction
{
    /// <summary>
    /// Forward (encoder) DC prediction. Replaces each entry in <paramref name="mbDc"/>
    /// with the residual: <c>actualDC - predictedDC</c>. Processes MBs in
    /// raster scan order so that neighbour predictions reference already-encoded
    /// actuals stored in <paramref name="predDc"/>.
    /// </summary>
    /// <param name="mbDc">DC values, dimensions [mbWidth, mbHeight, numComponents]. Updated in place to residuals.</param>
    /// <param name="predDc">Scratch buffer of the same shape used to remember actual DC values for neighbour reference. Caller owns lifetime.</param>
    /// <param name="format">Internal colour format — only YUV420 and YUV422 change the prediction-direction weighting.</param>
    /// <param name="leftEdgeMask">Optional [mbWidth, mbHeight] bool array marking tile-left edges. Null = single-tile (only x=0 is a left edge).</param>
    /// <param name="topEdgeMask">Optional [mbWidth, mbHeight] bool array marking tile-top edges. Null = single-tile (only y=0 is a top edge).</param>
    public static void Encode(
        int[,,] mbDc,
        int[,,] predDc,
        JxrInternalColorFormat format,
        bool[,]? leftEdgeMask = null,
        bool[,]? topEdgeMask = null,
        int[,]? mbDcMode = null)
    {
        var mbWidth = mbDc.GetLength(0);
        var mbHeight = mbDc.GetLength(1);
        var numComponents = mbDc.GetLength(2);

        for (var mby = 0; mby < mbHeight; mby++)
        {
            for (var mbx = 0; mbx < mbWidth; mbx++)
            {
                var leftEdge = leftEdgeMask?[mbx, mby] ?? (mbx == 0);
                var topEdge = topEdgeMask?[mbx, mby] ?? (mby == 0);

                var mode = CalcMode(predDc, mbx, mby, leftEdge, topEdge, format, numComponents);
                if (mbDcMode is not null) mbDcMode[mbx, mby] = mode;

                // Save actual DC values to predDc BEFORE subtracting — downstream
                // neighbours need the actual, not the residual.
                for (var c = 0; c < numComponents; c++)
                    predDc[mbx, mby, c] = mbDc[mbx, mby, c];

                ApplyPrediction(mbDc, predDc, mbx, mby, mode, format, numComponents, addNotSubtract: false);
            }
        }
    }

    /// <summary>
    /// Inverse (decoder) DC prediction — T.832 9.6.1.4. Adds back the prediction
    /// computed from neighbours, reconstructing the actual DC values in place.
    /// </summary>
    public static void Decode(
        int[,,] mbDc,
        int[,,] predDc,
        JxrInternalColorFormat format,
        bool[,]? leftEdgeMask = null,
        bool[,]? topEdgeMask = null,
        int[,]? mbDcMode = null)
    {
        var mbWidth = mbDc.GetLength(0);
        var mbHeight = mbDc.GetLength(1);
        var numComponents = mbDc.GetLength(2);

        for (var mby = 0; mby < mbHeight; mby++)
        {
            for (var mbx = 0; mbx < mbWidth; mbx++)
            {
                var leftEdge = leftEdgeMask?[mbx, mby] ?? (mbx == 0);
                var topEdge = topEdgeMask?[mbx, mby] ?? (mby == 0);

                var mode = CalcMode(predDc, mbx, mby, leftEdge, topEdge, format, numComponents);
                if (mbDcMode is not null) mbDcMode[mbx, mby] = mode;

                // Decoder: residual + prediction = actual. Add first, THEN save to predDc.
                ApplyPrediction(mbDc, predDc, mbx, mby, mode, format, numComponents, addNotSubtract: true);

                for (var c = 0; c < numComponents; c++)
                    predDc[mbx, mby, c] = mbDc[mbx, mby, c];
            }
        }
    }

    /// <summary>
    /// T.832 9.6.1.3 / Table 128 — pick prediction mode from neighbour DC differences.
    /// Returns 0=left, 1=top, 2=both, 3=none.
    /// </summary>
    private static int CalcMode(
        int[,,] predDc,
        int mbx, int mby,
        bool leftEdge, bool topEdge,
        JxrInternalColorFormat format,
        int numComponents)
    {
        if (leftEdge && topEdge) return 3; // No prediction
        if (leftEdge) return 1;            // Only top neighbour available
        if (topEdge) return 0;             // Only left neighbour available

        // Both neighbours available — pick from gradient comparison.
        var iLeft = predDc[mbx - 1, mby, 0];
        var iTop = predDc[mbx, mby - 1, 0];
        var iTopLeft = predDc[mbx - 1, mby - 1, 0];

        int strHor, strVer;
        if ((format == JxrInternalColorFormat.YOnly || format == JxrInternalColorFormat.NComponent)
            || numComponents < 3)
        {
            // No chroma weighting.
            strHor = Abs(iTopLeft - iLeft);
            strVer = Abs(iTopLeft - iTop);
        }
        else
        {
            // Chroma-aware gradient. T.832 9.6.1.3: iScale weights luma vs U+V.
            var iScale = format switch
            {
                JxrInternalColorFormat.YUV420 => 8,
                JxrInternalColorFormat.YUV422 => 4,
                _ => 2,
            };
            var iLeftU = predDc[mbx - 1, mby, 1];
            var iTopU = predDc[mbx, mby - 1, 1];
            var iTopLeftU = predDc[mbx - 1, mby - 1, 1];
            var iLeftV = predDc[mbx - 1, mby, 2];
            var iTopV = predDc[mbx, mby - 1, 2];
            var iTopLeftV = predDc[mbx - 1, mby - 1, 2];

            strHor = Abs(iTopLeft - iLeft) * iScale + Abs(iTopLeftU - iLeftU) + Abs(iTopLeftV - iLeftV);
            strVer = Abs(iTopLeft - iTop) * iScale + Abs(iTopLeftU - iTopU) + Abs(iTopLeftV - iTopV);
        }

        const int iOrWt = 4;
        if (strHor * iOrWt < strVer) return 1; // Vertical gradient dominates → predict from top
        if (strVer * iOrWt < strHor) return 0; // Horizontal gradient dominates → predict from left
        return 2;                               // Roughly isotropic → average both
    }

    /// <summary>
    /// T.832 9.6.1.4 / Table 129 — apply the prediction delta. The encoder
    /// subtracts (residual = actual - prediction), the decoder adds
    /// (actual = residual + prediction); the formula is identical, only the
    /// sign flips. <paramref name="addNotSubtract"/> selects direction.
    /// </summary>
    private static void ApplyPrediction(
        int[,,] mbDc,
        int[,,] predDc,
        int mbx, int mby,
        int mode,
        JxrInternalColorFormat format,
        int numComponents,
        bool addNotSubtract)
    {
        if (mode == 3) return; // No prediction

        var isYuvChroma = format == JxrInternalColorFormat.YUV420 || format == JxrInternalColorFormat.YUV422;

        for (var c = 0; c < numComponents; c++)
        {
            int prediction;
            if (mode == 0)
            {
                prediction = predDc[mbx - 1, mby, c];
            }
            else if (mode == 1)
            {
                prediction = predDc[mbx, mby - 1, c];
            }
            else // mode == 2: average
            {
                var iLeft = predDc[mbx - 1, mby, c];
                var iTop = predDc[mbx, mby - 1, c];
                // Chroma in YUV420/YUV422 rounds half up; luma and everything else floor.
                prediction = (c != 0 && isYuvChroma)
                    ? (iTop + iLeft + 1) >> 1
                    : (iTop + iLeft) >> 1;
            }

            if (addNotSubtract)
                mbDc[mbx, mby, c] += prediction;
            else
                mbDc[mbx, mby, c] -= prediction;
        }
    }

    private static int Abs(int x) => x < 0 ? -x : x;
}
