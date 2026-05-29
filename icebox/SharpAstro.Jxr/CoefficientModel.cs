namespace SharpAstro.Jxr;

/// <summary>
/// Per-macroblock state for adaptive coefficient normalization — T.832 §8.12.
/// </summary>
/// <remarks>
/// Coefficients are split into a VLC-coded part (the leading "level index")
/// and a fixed-length-coded part (the remaining low bits). The model
/// tracks how many bits go into the FLC part for each of up to two channel
/// groups (luma + chroma), adapting based on running statistics so that
/// high-energy regions widen the FLC and low-energy regions narrow it.
///
/// Two state pairs <see cref="MState0"/>/<see cref="MState1"/> and
/// <see cref="MBits0"/>/<see cref="MBits1"/>: index 0 is luma (and the
/// only used pair for Y_ONLY), index 1 is chroma (used for everything else).
/// </remarks>
public struct CoefficientModelState
{
    public int MState0;
    public int MState1;
    public int MBits0;
    public int MBits1;
}

/// <summary>
/// Initialise and update <see cref="CoefficientModelState"/> — T.832
/// 8.12.1 (<c>InitializeModelMB</c>) and 8.12.2 (<c>UpdateModelMB</c>).
/// </summary>
public static class CoefficientModel
{
    /// <summary>Frequency band the model is tracking — selects initial MBits and update weights.</summary>
    public enum Band
    {
        Dc = 0,
        Lp = 1,
        Hp = 2,
    }

    /// <summary>
    /// T.832 8.12.1 / Table 115. <c>MBits</c> starts at <c>(2 - band) * 4</c>
    /// so DC = 8, LP = 4, HP = 0 — high-energy bands begin with wider FLC slots.
    /// </summary>
    public static CoefficientModelState Initialize(Band band)
    {
        var initialBits = (2 - (int)band) * 4;
        return new CoefficientModelState
        {
            MState0 = 0,
            MState1 = 0,
            MBits0 = initialBits,
            MBits1 = initialBits,
        };
    }

    // T.832 8.12.2 weight tables.
    // iWeight0 indexed by band — applied to the luma component.
    private static ReadOnlySpan<int> Weight0 => [240, 12, 1];

    // iWeight1 indexed by [band][NumComponents - 1] — applied to chroma when
    // INTERNAL_CLR_FMT is not YUV420 / YUV422. NumComponents ranges 1..16.
    private static readonly int[,] Weight1 = new int[3, 16]
    {
        // band 0 (DC)
        { 0, 240, 120, 80, 60, 48, 40, 34, 30, 27, 24, 22, 20, 18, 17, 16 },
        // band 1 (LP)
        { 0, 12, 6, 4, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1 },
        // band 2 (HP)
        { 0, 16, 8, 5, 4, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1 },
    };

    // iWeight2: YUV420 weights at [0..2], YUV422 weights at [3..5], indexed by band.
    private static ReadOnlySpan<int> Weight2 => [120, 37, 2, 120, 18, 1];

    /// <summary>
    /// T.832 8.12.2 / Table 116 — update model state from the per-band
    /// LapMean of the just-encoded MB. Adjusts MBits up (more FLC bits) or
    /// down (fewer) when MState wanders out of the deadband ±8.
    /// </summary>
    /// <param name="state">Mutable model state, updated in place.</param>
    /// <param name="iLapMean0">Luma LapMean.</param>
    /// <param name="iLapMean1">Chroma LapMean (used when not Y_ONLY).</param>
    /// <param name="band">Which frequency band this model tracks.</param>
    /// <param name="format">INTERNAL_CLR_FMT — controls how iLapMean is scaled.</param>
    /// <param name="numComponents">Total component count (used for the non-YUV chroma weight table).</param>
    public static void Update(
        ref CoefficientModelState state,
        int iLapMean0,
        int iLapMean1,
        Band band,
        JxrInternalColorFormat format,
        int numComponents)
    {
        const int iModelWeight = 70;
        var b = (int)band;

        iLapMean0 *= Weight0[b];

        if (format == JxrInternalColorFormat.YUV420)
            iLapMean1 *= Weight2[b];
        else if (format == JxrInternalColorFormat.YUV422)
            iLapMean1 *= Weight2[3 + b];
        else
        {
            // For RGB / NComponent / YUVK / YOnly, iWeight1 is indexed by [band][NumComponents - 1].
            var col = numComponents - 1;
            if (col < 0) col = 0;
            if (col > 15) col = 15;
            iLapMean1 *= Weight1[b, col];
            if (band == Band.Hp)
                iLapMean1 >>= 4;
        }

        var iNumModels = format == JxrInternalColorFormat.YOnly ? 1 : 2;

        for (var j = 0; j < iNumModels; j++)
        {
            var iMS = j == 0 ? state.MState0 : state.MState1;
            var iLapMean = j == 0 ? iLapMean0 : iLapMean1;
            var mBits = j == 0 ? state.MBits0 : state.MBits1;

            var iDelta = (iLapMean - iModelWeight) >> 2;

            if (iDelta <= -8)
            {
                iDelta += 4;
                if (iDelta < -16) iDelta = -16;
                iMS += iDelta;
                if (iMS < -8)
                {
                    if (mBits == 0)
                    {
                        iMS = -8;
                    }
                    else
                    {
                        iMS = 0;
                        mBits--;
                    }
                }
            }
            else if (iDelta >= 8)
            {
                iDelta -= 4;
                if (iDelta > 15) iDelta = 15;
                iMS += iDelta;
                if (iMS > 8)
                {
                    if (mBits >= 15)
                    {
                        mBits = 15;
                        iMS = 8;
                    }
                    else
                    {
                        iMS = 0;
                        mBits++;
                    }
                }
            }

            if (j == 0) { state.MState0 = iMS; state.MBits0 = mBits; }
            else        { state.MState1 = iMS; state.MBits1 = mBits; }
        }
    }
}
