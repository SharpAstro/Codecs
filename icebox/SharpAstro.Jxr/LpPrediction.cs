namespace SharpAstro.Jxr;

/// <summary>
/// Low-pass (LP) coefficient prediction across the macroblock grid — T.832 §9.6.2.
/// </summary>
/// <remarks>
/// LP prediction operates on the 15 LP coefficients per MB per colour component
/// (positions 1..15 of the DC-LP array; position 0 is the super-DC, handled by
/// <see cref="DcPrediction"/>). Three modes:
/// <list type="bullet">
///   <item>0 — predict from left neighbour (only when MBDCMode==0 and QP matches)</item>
///   <item>1 — predict from top neighbour (only when MBDCMode==1 and QP matches)</item>
///   <item>2 — no prediction (whenever the QP differs or DC was averaged/uninitialised)</item>
/// </list>
/// Only specific LP positions are predicted, and the set depends on colour
/// format. The luma/Y444/NComponent paths predict positions {1,2,3} from top
/// or {4,8,12} from left; YUV420 chroma predicts {1} from top or {2} from
/// left; YUV422 chroma has its own scheme including a within-MB chain
/// (position 5 predicted from position 1 of the same MB). See Table 133.
/// </remarks>
public static class LpPrediction
{
    /// <summary>
    /// Forward (encoder) LP prediction. Replaces affected LP positions with
    /// <c>actual - prediction</c> residuals; positions not subject to prediction
    /// stay untouched. <paramref name="predDcLp"/> is a scratch buffer the caller
    /// owns; the function uses positions 1..6 of it.
    /// </summary>
    public static void Encode(
        int[,,,] mbDcLp,
        int[,,,] predDcLp,
        int[,] mbDcMode,
        JxrInternalColorFormat format,
        int[,]? mbQpIndexLp = null)
    {
        Process(mbDcLp, predDcLp, mbDcMode, format, mbQpIndexLp, addNotSubtract: false);
    }

    /// <summary>
    /// Inverse (decoder) LP prediction — T.832 9.6.2.4. Adds back the prediction
    /// to reconstruct actual LP coefficients.
    /// </summary>
    public static void Decode(
        int[,,,] mbDcLp,
        int[,,,] predDcLp,
        int[,] mbDcMode,
        JxrInternalColorFormat format,
        int[,]? mbQpIndexLp = null)
    {
        Process(mbDcLp, predDcLp, mbDcMode, format, mbQpIndexLp, addNotSubtract: true);
    }

    private static void Process(
        int[,,,] mbDcLp,
        int[,,,] predDcLp,
        int[,] mbDcMode,
        JxrInternalColorFormat format,
        int[,]? mbQpIndexLp,
        bool addNotSubtract)
    {
        var mbWidth = mbDcLp.GetLength(0);
        var mbHeight = mbDcLp.GetLength(1);
        var numComponents = mbDcLp.GetLength(2);

        for (var mby = 0; mby < mbHeight; mby++)
        {
            for (var mbx = 0; mbx < mbWidth; mbx++)
            {
                var dcMode = mbDcMode[mbx, mby];
                var lpMode = CalcLpMode(mbx, mby, dcMode, mbQpIndexLp);

                if (addNotSubtract)
                {
                    // Decoder path: apply prediction (spec order), then update predDcLp.
                    ApplyLpPrediction(mbDcLp, predDcLp, mbx, mby, lpMode, dcMode, format, numComponents, addNotSubtract: true);
                    UpdatePredDcLp(mbDcLp, predDcLp, mbx, mby, format, numComponents);
                }
                else
                {
                    // Encoder path: subtract prediction. predDcLp must be populated
                    // with the actuals BEFORE the subtraction so neighbours see actuals.
                    UpdatePredDcLp(mbDcLp, predDcLp, mbx, mby, format, numComponents);
                    ApplyLpPrediction(mbDcLp, predDcLp, mbx, mby, lpMode, dcMode, format, numComponents, addNotSubtract: false);
                }

            }
        }
    }

    /// <summary>
    /// T.832 9.6.2.3 / Table 132 — LP mode is derived from DC mode + QP-equality.
    /// </summary>
    private static int CalcLpMode(int mbx, int mby, int dcMode, int[,]? mbQpIndexLp)
    {
        // No QP info → assume all MBs share the same QP index, so QP comparison is always equal.
        bool qpEqualLeft, qpEqualTop;
        if (mbQpIndexLp is null)
        {
            qpEqualLeft = true;
            qpEqualTop = true;
        }
        else
        {
            var curr = mbQpIndexLp[mbx, mby];
            qpEqualLeft = mbx > 0 && mbQpIndexLp[mbx - 1, mby] == curr;
            qpEqualTop = mby > 0 && mbQpIndexLp[mbx, mby - 1] == curr;
        }

        if (dcMode == 0 && qpEqualLeft) return 0;
        if (dcMode == 1 && qpEqualTop) return 1;
        return 2;
    }

    /// <summary>
    /// T.832 9.6.2.4 / Table 133 — apply the LP prediction delta. The decoder
    /// adds, the encoder subtracts. Order of operations matters for the YUV422
    /// chained case where position 5 references position 1 of the same MB.
    /// </summary>
    private static void ApplyLpPrediction(
        int[,,,] mbDcLp,
        int[,,,] predDcLp,
        int mbx, int mby,
        int lpMode, int dcMode,
        JxrInternalColorFormat format,
        int numComponents,
        bool addNotSubtract)
    {
        for (var c = 0; c < numComponents; c++)
        {
            var isLuma = c == 0;
            var isYuv420 = format == JxrInternalColorFormat.YUV420;
            var isYuv422 = format == JxrInternalColorFormat.YUV422;
            var isYuvChroma = !isLuma && (isYuv420 || isYuv422);

            if (!isYuvChroma)
            {
                // Luma / Y444 / NComponent / RGB etc. From jxrlib's reference
                // encoder (predMacroblockEnc, AD prediction) verified via
                // instrumented stderr trace on a 2-MB BD16F YUV444 input:
                //   * predict-from-LEFT (lpMode 0) subtracts at positions {1, 2, 3}
                //   * predict-from-TOP  (lpMode 1) subtracts at positions {4, 8, 12}
                // (Confirmed: MB 1 post-DC[1] = 135 + 127 = 262 where 127 = LEFT MB
                //  position 1, matching {1,2,3}-from-LEFT semantics.)
                if (lpMode == 0)
                {
                    Apply(mbDcLp, mbx, mby, c, 1, predDcLp[mbx - 1, mby, c, 1], addNotSubtract);
                    Apply(mbDcLp, mbx, mby, c, 2, predDcLp[mbx - 1, mby, c, 2], addNotSubtract);
                    Apply(mbDcLp, mbx, mby, c, 3, predDcLp[mbx - 1, mby, c, 3], addNotSubtract);
                }
                else if (lpMode == 1)
                {
                    Apply(mbDcLp, mbx, mby, c, 4, predDcLp[mbx, mby - 1, c, 4], addNotSubtract);
                    Apply(mbDcLp, mbx, mby, c, 8, predDcLp[mbx, mby - 1, c, 5], addNotSubtract);
                    Apply(mbDcLp, mbx, mby, c, 12, predDcLp[mbx, mby - 1, c, 6], addNotSubtract);
                }
            }
            else if (isYuv420)
            {
                // YUV420 chroma — single position predicted in each direction.
                if (lpMode == 0)
                    Apply(mbDcLp, mbx, mby, c, 2, predDcLp[mbx - 1, mby, c, 2], addNotSubtract);
                else if (lpMode == 1)
                    Apply(mbDcLp, mbx, mby, c, 1, predDcLp[mbx, mby - 1, c, 1], addNotSubtract);
            }
            else // YUV422 chroma
            {
                if (lpMode == 0)
                {
                    Apply(mbDcLp, mbx, mby, c, 4, predDcLp[mbx - 1, mby, c, 4], addNotSubtract);
                    Apply(mbDcLp, mbx, mby, c, 2, predDcLp[mbx - 1, mby, c, 2], addNotSubtract);
                    Apply(mbDcLp, mbx, mby, c, 6, predDcLp[mbx - 1, mby, c, 6], addNotSubtract);
                }
                else if (lpMode == 1)
                {
                    // Spec order: [4] += top[4]; [1] += top[5]; [5] += own[1].
                    // The third step uses the AFTER-prediction value of [1], so
                    // the encoder must reverse the order to subtract correctly.
                    if (addNotSubtract)
                    {
                        Apply(mbDcLp, mbx, mby, c, 4, predDcLp[mbx, mby - 1, c, 4], addNotSubtract: true);
                        Apply(mbDcLp, mbx, mby, c, 1, predDcLp[mbx, mby - 1, c, 5], addNotSubtract: true);
                        Apply(mbDcLp, mbx, mby, c, 5, mbDcLp[mbx, mby, c, 1], addNotSubtract: true);
                    }
                    else
                    {
                        // Encoder: undo the chain first (while [1] is still the actual).
                        Apply(mbDcLp, mbx, mby, c, 5, mbDcLp[mbx, mby, c, 1], addNotSubtract: false);
                        Apply(mbDcLp, mbx, mby, c, 1, predDcLp[mbx, mby - 1, c, 5], addNotSubtract: false);
                        Apply(mbDcLp, mbx, mby, c, 4, predDcLp[mbx, mby - 1, c, 4], addNotSubtract: false);
                    }
                }
                else if (dcMode == 1)
                {
                    // YUV422 special: LP mode 2 (no prediction), but DC came from top —
                    // position 5 is still predicted from position 1 of the same MB.
                    Apply(mbDcLp, mbx, mby, c, 5, mbDcLp[mbx, mby, c, 1], addNotSubtract);
                }
            }
        }
    }

    private static void Apply(int[,,,] mbDcLp, int mbx, int mby, int c, int pos, int prediction, bool addNotSubtract)
    {
        if (addNotSubtract)
            mbDcLp[mbx, mby, c, pos] += prediction;
        else
            mbDcLp[mbx, mby, c, pos] -= prediction;
    }

    /// <summary>
    /// T.832 9.6.2.5 / Table 134 — snapshot selected MbDCLP positions into
    /// PredDCLP for use by downstream neighbour MBs. The luma path "remaps"
    /// positions 8 and 12 of MbDCLP into positions 5 and 6 of PredDCLP.
    /// </summary>
    private static void UpdatePredDcLp(
        int[,,,] mbDcLp, int[,,,] predDcLp,
        int mbx, int mby,
        JxrInternalColorFormat format,
        int numComponents)
    {
        for (var c = 0; c < numComponents; c++)
        {
            var isLuma = c == 0;
            var isYuv420 = format == JxrInternalColorFormat.YUV420;
            var isYuv422 = format == JxrInternalColorFormat.YUV422;

            if (isLuma || (!isYuv420 && !isYuv422))
            {
                predDcLp[mbx, mby, c, 1] = mbDcLp[mbx, mby, c, 1];
                predDcLp[mbx, mby, c, 2] = mbDcLp[mbx, mby, c, 2];
                predDcLp[mbx, mby, c, 3] = mbDcLp[mbx, mby, c, 3];
                predDcLp[mbx, mby, c, 4] = mbDcLp[mbx, mby, c, 4];
                predDcLp[mbx, mby, c, 5] = mbDcLp[mbx, mby, c, 8];   // remap
                predDcLp[mbx, mby, c, 6] = mbDcLp[mbx, mby, c, 12];  // remap
            }
            else if (isYuv420)
            {
                predDcLp[mbx, mby, c, 1] = mbDcLp[mbx, mby, c, 1];
                predDcLp[mbx, mby, c, 2] = mbDcLp[mbx, mby, c, 2];
            }
            else // YUV422 chroma
            {
                predDcLp[mbx, mby, c, 1] = mbDcLp[mbx, mby, c, 1];
                predDcLp[mbx, mby, c, 2] = mbDcLp[mbx, mby, c, 2];
                predDcLp[mbx, mby, c, 4] = mbDcLp[mbx, mby, c, 4];
                predDcLp[mbx, mby, c, 5] = mbDcLp[mbx, mby, c, 5];
                predDcLp[mbx, mby, c, 6] = mbDcLp[mbx, mby, c, 6];
            }
        }
    }
}
