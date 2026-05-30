namespace SharpAstro.Jxl;

/// <summary>JPEG XL Modular predictor (ISO/IEC 18181-1 §H.5). Values 0–13; 14+ are invalid.</summary>
internal enum JxlPredictor
{
    Zero = 0,
    West = 1,
    North = 2,
    AvgWestAndNorth = 3,
    Select = 4,
    Gradient = 5,
    SelfCorrecting = 6,
    NorthEast = 7,
    NorthWest = 8,
    WestWest = 9,
    AvgWestAndNorthWest = 10,
    AvgNorthAndNorthWest = 11,
    AvgNorthAndNorthEast = 12,
    AvgAll = 13,
}

/// <summary>The decoded neighbours of the current sample, in Modular convention.</summary>
internal readonly struct JxlNeighbors
{
    public required int N { get; init; }
    public required int W { get; init; }
    public required int NW { get; init; }
    public required int NE { get; init; }
    public required int NN { get; init; }
    public required int WW { get; init; }
    public required int NEE { get; init; }
}

/// <summary>
/// Stateless Modular predictor formulas (jxl-modular predictor.rs). The SelfCorrecting (weighted)
/// predictor is stateful and supplied here as its already-computed ×8 prediction; its state
/// machine lives in the predictor-state porting step. All averages divide with truncation toward
/// zero (matching Rust i64 division); intermediates are i64 to avoid overflow before the cast.
/// </summary>
internal static class JxlModularPredictor
{
    public static int Predict(JxlPredictor predictor, in JxlNeighbors nb, long wpPredictionTimes8 = 0)
    {
        long n = nb.N, w = nb.W, nw = nb.NW, ne = nb.NE, nn = nb.NN, ww = nb.WW, nee = nb.NEE;
        return predictor switch
        {
            JxlPredictor.Zero => 0,
            JxlPredictor.West => (int)w,
            JxlPredictor.North => (int)n,
            JxlPredictor.AvgWestAndNorth => (int)((w + n) / 2),
            JxlPredictor.Select => Math.Abs(n - nw) < Math.Abs(w - nw) ? (int)w : (int)n,
            JxlPredictor.Gradient => (int)Math.Clamp(n + w - nw, Math.Min(w, n), Math.Max(w, n)),
            JxlPredictor.SelfCorrecting => (int)((wpPredictionTimes8 + 3) >> 3),
            JxlPredictor.NorthEast => (int)ne,
            JxlPredictor.NorthWest => (int)nw,
            JxlPredictor.WestWest => (int)ww,
            JxlPredictor.AvgWestAndNorthWest => (int)((w + nw) / 2),
            JxlPredictor.AvgNorthAndNorthWest => (int)((n + nw) / 2),
            JxlPredictor.AvgNorthAndNorthEast => (int)((n + ne) / 2),
            JxlPredictor.AvgAll => (int)((6 * n - 2 * nn + 7 * w + ww + nee + 3 * ne + 8) / 16),
            _ => throw new InvalidDataException($"JPEG XL invalid Modular predictor {(int)predictor}."),
        };
    }
}
