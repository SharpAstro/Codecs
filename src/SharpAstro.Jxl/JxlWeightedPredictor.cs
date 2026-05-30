using System.Numerics;

namespace SharpAstro.Jxl;

/// <summary>
/// JPEG XL self-correcting (weighted) predictor (ISO/IEC 18181-1 §H.5.1, jxl-modular predictor.rs).
/// Maintains four sub-predictors and per-pixel error accumulators; the prediction is an
/// error-weighted blend, clamped when neighbour errors agree in sign. All intermediate arithmetic
/// is i64/u32 exactly as the reference, with explicit wrapping where it wraps.
/// </summary>
internal sealed class JxlWeightedPredictor
{
    private static readonly uint[] DivLookup = BuildDivLookup();

    private static uint[] BuildDivLookup()
    {
        var t = new uint[65];
        for (int i = 1; i <= 64; i++)
            t[i] = (uint)((1 << 24) / i);
        return t;
    }

    private readonly int _p1, _p2, _p3a, _p3b, _p3c, _p3d, _p3e;
    private readonly uint _w0, _w1, _w2, _w3;

    private int _width;
    private int _x, _y;
    private int[] _trueErrRow = [];
    private Subpred4[] _subErrRow = [];
    private int _trueErrW, _trueErrNw, _trueErrN, _trueErrNe;
    private Subpred4 _subErrNwWw, _subErrNW, _subErrNe;

    private struct Subpred4
    {
        public uint E0, E1, E2, E3;
    }

    /// <summary>The weighted prediction (×8 fixed point), its max neighbour error, and the four sub-predictions.</summary>
    public struct Prediction
    {
        public long Value;
        public int MaxError;
        public long S0, S1, S2, S3;
    }

    public JxlWeightedPredictor(in JxlWpHeader wp)
    {
        _p1 = wp.P1; _p2 = wp.P2; _p3a = wp.P3a; _p3b = wp.P3b; _p3c = wp.P3c; _p3d = wp.P3d; _p3e = wp.P3e;
        _w0 = (uint)wp.W0; _w1 = (uint)wp.W1; _w2 = (uint)wp.W2; _w3 = (uint)wp.W3;
    }

    public void Reset(int width)
    {
        _width = width;
        _x = 0;
        _y = 0;
        _trueErrRow = new int[width];
        _subErrRow = new Subpred4[width];
        _trueErrW = _trueErrNw = _trueErrN = _trueErrNe = 0;
        _subErrNwWw = default;
        _subErrNW = default;
        _subErrNe = default;
    }

    public Prediction Predict(int n, int nw, int ne, int w, int nn)
    {
        long n3 = (long)n << 3, nw3 = (long)nw << 3, ne3 = (long)ne << 3, w3 = (long)w << 3, nn3 = (long)nn << 3;
        long ew = _trueErrW, enw = _trueErrNw, en = _trueErrN, ene = _trueErrNe;

        long s0 = w3 + ne3 - n3;
        long s1 = n3 - ((ew + en + ene) * _p1 >> 5);
        long s2 = w3 - ((ew + en + enw) * _p2 >> 5);
        long s3 = n3 - ((enw * _p3a + en * _p3b + ene * _p3c + (nn3 - n3) * _p3d + (nw3 - w3) * _p3e) >> 5);

        Span<long> sub = [s0, s1, s2, s3];
        Span<uint> errSum =
        [
            unchecked(_subErrNwWw.E0 + _subErrNW.E0 + _subErrNe.E0),
            unchecked(_subErrNwWw.E1 + _subErrNW.E1 + _subErrNe.E1),
            unchecked(_subErrNwWw.E2 + _subErrNW.E2 + _subErrNe.E2),
            unchecked(_subErrNwWw.E3 + _subErrNW.E3 + _subErrNe.E3),
        ];
        Span<uint> wpWn = [_w0, _w1, _w2, _w3];
        Span<uint> weight = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
        {
            int shift = ILog2(((ulong)errSum[i] + 1) >> 5);
            weight[i] = 4u + ((wpWn[i] * DivLookup[(errSum[i] >> shift) + 1]) >> shift);
        }

        uint sumWeights = weight[0] + weight[1] + weight[2] + weight[3];
        int logWeight = ILog2((ulong)sumWeights >> 4);
        for (int i = 0; i < 4; i++)
            weight[i] >>= logWeight;
        sumWeights = weight[0] + weight[1] + weight[2] + weight[3];

        long s = (long)(sumWeights >> 1) - 1;
        for (int i = 0; i < 4; i++)
            s += sub[i] * weight[i];
        long prediction = (s * DivLookup[sumWeights]) >> 24;

        if (((en ^ ew) | (en ^ enw)) <= 0)
        {
            long lo = Math.Min(Math.Min(n3, w3), ne3);
            long hi = Math.Max(Math.Max(n3, w3), ne3);
            prediction = Math.Clamp(prediction, lo, hi);
        }

        long maxErr = ew;
        foreach (long e in (ReadOnlySpan<long>)[en, enw, ene])
            if (Math.Abs(e) > Math.Abs(maxErr))
                maxErr = e;

        return new Prediction { Value = prediction, MaxError = (int)maxErr, S0 = s0, S1 = s1, S2 = s2, S3 = s3 };
    }

    public void Record(in Prediction pred, int sample)
    {
        long s8 = (long)sample << 3;
        long trueErr = pred.Value - s8;
        Subpred4 subErr;
        subErr.E0 = SubErr(pred.S0, s8);
        subErr.E1 = SubErr(pred.S1, s8);
        subErr.E2 = SubErr(pred.S2, s8);
        subErr.E3 = SubErr(pred.S3, s8);

        _trueErrRow[_x] = (int)trueErr;
        _subErrRow[_x] = subErr;
        _x++;

        if (_x >= _width)
        {
            _y++;
            _x = 0;
            _trueErrW = 0;
            _trueErrN = _trueErrRow[0];
            _trueErrNw = _trueErrN;
            _subErrNW = _subErrRow[0];
            _subErrNwWw = _subErrNW;
            if (_width <= 1)
            {
                _trueErrNe = _trueErrN;
                _subErrNe = _subErrNW;
            }
            else
            {
                _trueErrNe = _trueErrRow[1];
                _subErrNe = _subErrRow[1];
            }
        }
        else
        {
            _trueErrW = (int)trueErr;
            _trueErrNw = _trueErrN;
            _trueErrN = _trueErrNe;
            _subErrNwWw = _subErrNW;
            _subErrNW = _subErrNe;
            _subErrNW.E0 = unchecked(_subErrNW.E0 + subErr.E0);
            _subErrNW.E1 = unchecked(_subErrNW.E1 + subErr.E1);
            _subErrNW.E2 = unchecked(_subErrNW.E2 + subErr.E2);
            _subErrNW.E3 = unchecked(_subErrNW.E3 + subErr.E3);
            if (_x + 1 >= _width)
            {
                _trueErrNe = _trueErrN;
                _subErrNe = _subErrNW;
            }
            else if (_y != 0)
            {
                _trueErrNe = _trueErrRow[_x + 1];
                _subErrNe = _subErrRow[_x + 1];
            }
        }
    }

    private static uint SubErr(long subpred, long s8)
    {
        ulong d = (ulong)Math.Abs(subpred - s8);
        return (uint)((d + 3) >> 3);
    }

    private static int ILog2(ulong v) => v == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(v);
}
