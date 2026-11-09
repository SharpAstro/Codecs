namespace SharpAstro.Heif.Hevc;

/// <summary>
/// The in-loop filters of H.265 8.7, applied to the whole picture once its slices are decoded (every intra
/// prediction has read the unfiltered samples by then, as 8.7 requires): the deblocking filter (8.7.2),
/// vertical edges of the whole picture first and then horizontal edges over the result, then sample
/// adaptive offset (8.7.3) over the deblocked picture, with each CTB's SAO parameters as its
/// <c>sao()</c> syntax (7.3.8.3) gave them.
/// <para>
/// In an intra picture every edge on the 8x8 grid that is a transform block edge has boundary strength 2
/// (8.7.2.4: one side is intra), and those are the only edges: an intra prediction block's edges on that
/// grid are transform edges too (an NxN split forces the transform split). PCM and transquant bypass,
/// which exempt samples from both filters, are refused with the envelope.
/// </para>
/// </summary>
internal sealed partial class HevcPictureDecoder
{
    /// <summary>Table 8-12, beta' by Q = 0..51.</summary>
    private static ReadOnlySpan<byte> BetaTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64,
    ];

    /// <summary>Table 8-12, tC' by Q = 0..53.</summary>
    private static ReadOnlySpan<byte> TcTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6,
        7, 8, 9, 10, 11, 13, 14, 16, 18, 20, 22, 24,
    ];

    private const int IntraBoundaryStrength = 2;

    /// <summary>Records a transform block's left and top edges where they lie on the 8x8 deblocking grid.</summary>
    private void MarkTransformEdges(int x0, int y0, int size)
    {
        var w = Math.Min(size, _width - x0);
        var h = Math.Min(size, _height - y0);
        if ((x0 & 7) == 0)
            for (var j = 0; j < h; j += 4) _edges[Block(x0, y0 + j)] |= 1;
        if ((y0 & 7) == 0)
            for (var i = 0; i < w; i += 4) _edges[Block(x0 + i, y0)] |= 2;
    }

    // ---- sao() syntax (7.3.8.3) ---------------------------------------------------------------------

    private void Sao(int ctb, HevcSliceHeader header)
    {
        var rx = ctb % _widthInCtbs;
        var ry = ctb / _widthInCtbs;
        var sliceAddress = header.SegmentAddress; // no dependent slice segments: the slice starts where its segment does

        // Merging copies every component's parameters from the CTB to the left or above, inside the slice.
        if (rx > 0 && ctb - 1 >= sliceAddress && _cabac.Decision(HevcContext.SaoMergeFlag) == 1)
        {
            CopySao(ctb - 1, ctb);
            if (_statistics is not null) _statistics.SaoMergeLeft++;
            return;
        }
        if (ry > 0 && ctb - _widthInCtbs >= sliceAddress && _cabac.Decision(HevcContext.SaoMergeFlag) == 1)
        {
            CopySao(ctb - _widthInCtbs, ctb);
            if (_statistics is not null) _statistics.SaoMergeUp++;
            return;
        }

        var components = _chroma != 0 ? 3 : 1;
        for (var c = 0; c < components; c++)
        {
            var i = ctb * 3 + c;
            if (!(c == 0 ? header.SaoLuma : header.SaoChroma))
            {
                _saoType[i] = 0;
                continue;
            }

            // sao_type_idx_luma / _chroma: TR with cMax 2, the first bin context-coded and the second bypass.
            // Cr shares Cb's type (and, for edge offset, its class).
            int type;
            if (c == 2) type = _saoType[i - 1];
            else type = _cabac.Decision(HevcContext.SaoTypeIdx) == 0 ? 0 : _cabac.Bypass() == 0 ? 1 : 2;
            _saoType[i] = (byte)type;
            if (type == 0) continue;

            var bitDepth = c == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;
            var cMax = (1 << (Math.Min(bitDepth, 10) - 5)) - 1;
            Span<int> magnitude = stackalloc int[4];
            for (var k = 0; k < 4; k++)
            {
                var value = 0;
                while (value < cMax && _cabac.Bypass() == 1) value++; // sao_offset_abs: TR, bypass
                magnitude[k] = value;
                if (_statistics is not null) _statistics.SaoLargestOffset = Math.Max(_statistics.SaoLargestOffset, value);
            }

            var scale = c == 0 ? _pps.Log2SaoOffsetScaleLuma : _pps.Log2SaoOffsetScaleChroma;
            if (type == 1)
            {
                // Band offset: a sign for each non-zero offset, then the first of the four bands.
                for (var k = 0; k < 4; k++)
                    if (magnitude[k] != 0 && _cabac.Bypass() == 1) magnitude[k] = -magnitude[k];
                _saoBandOrClass[i] = (byte)_cabac.Bypass(5); // sao_band_position
                if (_statistics is not null)
                {
                    _statistics.SaoBand++;
                    _statistics.SaoBandPositions[_saoBandOrClass[i]]++;
                }
            }
            else
            {
                // Edge offset: the signs are fixed, positive for the two local-minimum categories and
                // negative for the two local-maximum ones; the class is coded for luma and for Cb.
                magnitude[2] = -magnitude[2];
                magnitude[3] = -magnitude[3];
                _saoBandOrClass[i] = c == 2 ? _saoBandOrClass[i - 1] : (byte)_cabac.Bypass(2);
                if (_statistics is not null) _statistics.SaoEdgeClasses[_saoBandOrClass[i]]++;
            }
            for (var k = 0; k < 4; k++) _saoOffsets[i * 4 + k] = (short)(magnitude[k] << scale);
        }
    }

    private void CopySao(int from, int to)
    {
        for (var c = 0; c < 3; c++)
        {
            _saoType[to * 3 + c] = _saoType[from * 3 + c];
            _saoBandOrClass[to * 3 + c] = _saoBandOrClass[from * 3 + c];
            for (var k = 0; k < 4; k++) _saoOffsets[(to * 3 + c) * 4 + k] = _saoOffsets[(from * 3 + c) * 4 + k];
        }
    }

    // ---- the filters -----------------------------------------------------------------------------------

    private void ApplyLoopFilters()
    {
        var deblock = false;
        var sao = false;
        foreach (var header in _sliceHeaders)
        {
            deblock |= !header.DeblockingDisabled;
            sao |= header.SaoLuma || header.SaoChroma;
        }
        if (!deblock && !sao) return;

        // A later slice may keep the filters off its left and upper boundary; checking that per sample is only
        // needed when one does. Lossless coding units are kept per sample too.
        _sliceBoundariesMatter = false;
        for (var s = 1; s < _sliceHeaders.Count; s++)
            _sliceBoundariesMatter |= !_sliceHeaders[s].LoopFilterAcrossSlices;

        if (deblock)
        {
            DeblockLuma(vertical: true);
            if (_chroma != 0) DeblockChroma(vertical: true);
            DeblockLuma(vertical: false);
            if (_chroma != 0) DeblockChroma(vertical: false);
        }
        if (sao)
        {
            SampleAdaptiveOffset(0, _luma, _width, _height, _sps.BitDepthLuma);
            if (_chroma != 0)
            {
                SampleAdaptiveOffset(1, _cb!, _chromaWidth, _chromaHeight, _sps.BitDepthChroma);
                SampleAdaptiveOffset(2, _cr!, _chromaWidth, _chromaHeight, _sps.BitDepthChroma);
            }
        }
    }

    private HevcSliceHeader SliceAt(int x, int y) => _sliceHeaders[_ctbSlice[CtbAddress(x, y)]];

    private bool _sliceBoundariesMatter;

    /// <summary>
    /// Whether a deblocking edge between p at (xp, yp) and q at (xq, yq) is filtered across a slice boundary:
    /// the edge is q's coding unit's, so q's slice decides (7.4.7.1: the flag covers a slice's left and
    /// upper boundary).
    /// </summary>
    private bool CrossesKeptSliceBoundary(int xp, int yp, int xq, int yq)
    {
        if (!_sliceBoundariesMatter) return false;
        int p = _ctbSlice[CtbAddress(xp, yp)], q = _ctbSlice[CtbAddress(xq, yq)];
        if (p == q || _sliceHeaders[q].LoopFilterAcrossSlices) return false;
        if (_statistics is not null) _statistics.SliceBoundariesKept++;
        return true;
    }

    /// <summary>
    /// 8.7.2.5.3 and 8.7.2.5.6-7 over every luma edge of one direction: four lines at a time, each segment
    /// belonging to the coding unit on its right (or below), whose slice gives the offsets.
    /// </summary>
    private void DeblockLuma(bool vertical)
    {
        var bitDepth = _sps.BitDepthLuma;
        var max = (1 << bitDepth) - 1;
        int across = vertical ? 1 : _width, along = vertical ? _width : 1;
        var bit = vertical ? 1 : 2;
        for (var y = vertical ? 0 : 8; y < _height; y += vertical ? 4 : 8)
        {
            for (var x = vertical ? 8 : 0; x < _width; x += vertical ? 8 : 4)
            {
                if ((_edges[Block(x, y)] & bit) == 0) continue;
                var slice = SliceAt(x, y);
                if (slice.DeblockingDisabled) continue;
                int xp = vertical ? x - 1 : x, yp = vertical ? y : y - 1;
                if (CrossesKeptSliceBoundary(xp, yp, x, y)) continue;
                int qpQ = _qpMap[Block(x, y)];
                int qpP = _qpMap[Block(xp, yp)];
                var qpL = (qpQ + qpP + 1) >> 1;
                var betaQ = Math.Clamp(qpL + (slice.BetaOffsetDiv2 << 1), 0, 51);
                var tcQ = Math.Clamp(qpL + 2 * (IntraBoundaryStrength - 1) + (slice.TcOffsetDiv2 << 1), 0, 53);
                if (_statistics is not null)
                {
                    _statistics.BetaIndices[betaQ]++;
                    _statistics.TcIndices[tcQ]++;
                }
                var beta = BetaTable[betaQ] << (bitDepth - 8);
                var tc = TcTable[tcQ] << (bitDepth - 8);
                FilterLumaSegment(_luma, y * _width + x, across, along, beta, tc, max,
                    keepP: _bypass[Block(xp, yp)] != 0, keepQ: _bypass[Block(x, y)] != 0);
            }
        }
    }

    /// <summary>
    /// One luma edge segment of four lines: <paramref name="at"/> is q0 of line 0, p<sub>i</sub> lies
    /// <c>(i + 1) * across</c> before a line's q0 and q<sub>i</sub> <c>i * across</c> after it, and line k
    /// starts <c>k * along</c> after line 0.
    /// </summary>
    private void FilterLumaSegment(ushort[] s, int at, int across, int along, int beta, int tc, int max, bool keepP, bool keepQ)
    {
        // A lossless coding unit's samples are kept (nDp or nDq 0, 8.7.2.5.7): computed from, never written.
        if ((keepP || keepQ) && _statistics is not null) _statistics.FilteringSkippedForBypass++;
        void SetP(int i, int k, int value) { if (!keepP) s[at + k * along - (i + 1) * across] = (ushort)value; }
        void SetQ(int i, int k, int value) { if (!keepQ) s[at + k * along + i * across] = (ushort)value; }
        int P(int i, int k) => s[at + k * along - (i + 1) * across];
        int Q(int i, int k) => s[at + k * along + i * across];

        // 8.7.2.5.3: the decisions, from lines 0 and 3.
        var dp0 = Math.Abs(P(2, 0) - 2 * P(1, 0) + P(0, 0));
        var dp3 = Math.Abs(P(2, 3) - 2 * P(1, 3) + P(0, 3));
        var dq0 = Math.Abs(Q(2, 0) - 2 * Q(1, 0) + Q(0, 0));
        var dq3 = Math.Abs(Q(2, 3) - 2 * Q(1, 3) + Q(0, 3));
        var dpq0 = dp0 + dq0;
        var dpq3 = dp3 + dq3;
        var d = dpq0 + dpq3;
        if (d >= beta)
        {
            if (_statistics is not null) _statistics.DeblockSkipped++;
            return;
        }

        // 8.7.2.5.6: a line may take the strong filter when it is smooth on both sides and the step is small.
        bool Strong(int k, int dpq) =>
            dpq < (beta >> 2)
            && Math.Abs(P(3, k) - P(0, k)) + Math.Abs(Q(0, k) - Q(3, k)) < (beta >> 3)
            && Math.Abs(P(0, k) - Q(0, k)) < ((5 * tc + 1) >> 1);

        var strong = Strong(0, 2 * dpq0) && Strong(3, 2 * dpq3);
        var side = (beta + (beta >> 1)) >> 3;
        var dEp = dp0 + dp3 < side;
        var dEq = dq0 + dq3 < side;

        // 8.7.2.5.7, each line.
        var any = false;
        var second = false;
        for (var k = 0; k < 4; k++)
        {
            var line = at + k * along;
            int p0 = P(0, k), p1 = P(1, k), p2 = P(2, k), p3 = P(3, k);
            int q0 = Q(0, k), q1 = Q(1, k), q2 = Q(2, k), q3 = Q(3, k);
            if (strong)
            {
                var c = 2 * tc;
                SetP(0, k, Math.Clamp((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3, p0 - c, p0 + c));
                SetP(1, k, Math.Clamp((p2 + p1 + p0 + q0 + 2) >> 2, p1 - c, p1 + c));
                SetP(2, k, Math.Clamp((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3, p2 - c, p2 + c));
                SetQ(0, k, Math.Clamp((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3, q0 - c, q0 + c));
                SetQ(1, k, Math.Clamp((p0 + q0 + q1 + q2 + 2) >> 2, q1 - c, q1 + c));
                SetQ(2, k, Math.Clamp((p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3, q2 - c, q2 + c));
                any = true;
                continue;
            }

            var delta = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
            if (Math.Abs(delta) >= tc * 10) continue;
            delta = Math.Clamp(delta, -tc, tc);
            SetP(0, k, Math.Clamp(p0 + delta, 0, max));
            SetQ(0, k, Math.Clamp(q0 - delta, 0, max));
            any = true;
            var half = tc >> 1;
            if (dEp)
            {
                var deltaP = Math.Clamp((((p2 + p0 + 1) >> 1) - p1 + delta) >> 1, -half, half);
                SetP(1, k, Math.Clamp(p1 + deltaP, 0, max));
                second = true;
            }
            if (dEq)
            {
                var deltaQ = Math.Clamp((((q2 + q0 + 1) >> 1) - q1 - delta) >> 1, -half, half);
                SetQ(1, k, Math.Clamp(q1 + deltaQ, 0, max));
                second = true;
            }
        }

        if (_statistics is not null)
        {
            if (strong) _statistics.DeblockStrong++;
            else if (any) _statistics.DeblockWeak++;
            else _statistics.DeblockSkipped++;
            if (second) _statistics.DeblockWeakSecondSample++;
        }
    }

    /// <summary>
    /// 8.7.2.5.5 over every chroma edge of one direction that lies on the 8x8 grid of chroma samples, four
    /// chroma lines at a time. Only boundary strength 2 edges are filtered, which in an intra picture is all
    /// of them; the QP is the luma QPs' mean plus the PPS chroma offset (not the slice's), through Table 8-10
    /// for 4:2:0.
    /// </summary>
    private void DeblockChroma(bool vertical)
    {
        var bitDepth = _sps.BitDepthChroma;
        var max = (1 << bitDepth) - 1;
        int across = vertical ? 1 : _chromaWidth, along = vertical ? _chromaWidth : 1;
        var bit = vertical ? 1 : 2;
        for (var cy = vertical ? 0 : 8; cy < _chromaHeight; cy += vertical ? 4 : 8)
        {
            for (var cx = vertical ? 8 : 0; cx < _chromaWidth; cx += vertical ? 8 : 4)
            {
                int x = cx << _log2SubW, y = cy << _log2SubH;
                if ((_edges[Block(x, y)] & bit) == 0) continue;
                var slice = SliceAt(x, y);
                if (slice.DeblockingDisabled) continue;
                int xp = vertical ? x - 1 : x, yp = vertical ? y : y - 1;
                if (CrossesKeptSliceBoundary(xp, yp, x, y)) continue;
                int qpQ = _qpMap[Block(x, y)];
                int qpP = _qpMap[Block(xp, yp)];
                bool keepP = _bypass[Block(xp, yp)] != 0, keepQ = _bypass[Block(x, y)] != 0;
                for (var c = 1; c <= 2; c++)
                {
                    var qpi = ((qpQ + qpP + 1) >> 1) + (c == 1 ? _pps.CbQpOffset : _pps.CrQpOffset);
                    int qpc;
                    if (_chroma == 1) qpc = qpi < 30 ? qpi : qpi > 43 ? qpi - 6 : ChromaQp420[qpi - 30];
                    else qpc = Math.Min(qpi, 51);
                    var tcQ = Math.Clamp(qpc + 2 * (IntraBoundaryStrength - 1) + (slice.TcOffsetDiv2 << 1), 0, 53);
                    if (_statistics is not null) _statistics.TcIndices[tcQ]++;
                    var tc = TcTable[tcQ] << (bitDepth - 8);
                    var plane = c == 1 ? _cb! : _cr!;
                    var at = cy * _chromaWidth + cx;
                    for (var k = 0; k < 4; k++)
                    {
                        var line = at + k * along;
                        int p0 = plane[line - across], p1 = plane[line - 2 * across];
                        int q0 = plane[line], q1 = plane[line + across];
                        var delta = Math.Clamp((((q0 - p0) << 2) + p1 - q1 + 4) >> 3, -tc, tc);
                        // Counted where keeping a lossless side actually prevents a change.
                        if (delta != 0 && (keepP || keepQ) && _statistics is not null) _statistics.ChromaDeblockKeptForBypass++;
                        if (!keepP) plane[line - across] = (ushort)Math.Clamp(p0 + delta, 0, max);
                        if (!keepQ) plane[line] = (ushort)Math.Clamp(q0 - delta, 0, max);
                    }
                    if (_statistics is not null) _statistics.DeblockChroma++;
                }
            }
        }
    }

    /// <summary>
    /// 8.7.3: a neighbour in another slice is not used when the boundary between them is kept, which the
    /// later of the two slices decides: the current sample's slice if the neighbour precedes it in decoding
    /// order, the neighbour's if it follows.
    /// </summary>
    private bool SaoNeighbourKept(int x, int y, int nx, int ny, int log2W, int log2H)
    {
        int current = _ctbSlice[CtbAddress(x << log2W, y << log2H)], neighbour = _ctbSlice[CtbAddress(nx << log2W, ny << log2H)];
        if (current == neighbour) return false;
        var kept = !_sliceHeaders[Math.Max(current, neighbour)].LoopFilterAcrossSlices;
        if (kept && _statistics is not null) _statistics.SliceBoundariesKept++;
        return kept;
    }

    /// <summary>
    /// 8.7.3 for one component: each CTB's samples offset by band or by edge category, read from a copy of
    /// the deblocked plane so every sample sees its neighbours as deblocking left them. An edge neighbour
    /// outside the picture leaves the sample as it is.
    /// </summary>
    private void SampleAdaptiveOffset(int c, ushort[] plane, int width, int height, int bitDepth)
    {
        var source = (ushort[])plane.Clone();
        var max = (1 << bitDepth) - 1;
        int log2W = c == 0 ? 0 : _log2SubW, log2H = c == 0 ? 0 : _log2SubH;
        int ctbWidth = (1 << _log2Ctb) >> log2W, ctbHeight = (1 << _log2Ctb) >> log2H;
        Span<int> bandTable = stackalloc int[32];
        Span<int> offsets = stackalloc int[5];

        for (var ctb = 0; ctb < _widthInCtbs * _heightInCtbs; ctb++)
        {
            var i = ctb * 3 + c;
            var type = _saoType[i];
            if (type == 0) continue;
            offsets[0] = 0;
            for (var k = 0; k < 4; k++) offsets[k + 1] = _saoOffsets[i * 4 + k];
            int x0 = ctb % _widthInCtbs * ctbWidth, y0 = ctb / _widthInCtbs * ctbHeight;
            int x1 = Math.Min(x0 + ctbWidth, width), y1 = Math.Min(y0 + ctbHeight, height);

            if (type == 1)
            {
                // Band offset: the four bands from sao_band_position take offsets 1..4, the other 28 none.
                bandTable.Clear();
                for (var k = 0; k < 4; k++) bandTable[(k + _saoBandOrClass[i]) & 31] = k + 1;
                var shift = bitDepth - 5;
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        if (_bypass[Block(x << log2W, y << log2H)] != 0)
                        {
                            if (_statistics is not null) _statistics.SaoSamplesKeptForBypass++;
                            continue; // lossless: kept (8.7.3)
                        }
                        var v = source[y * width + x];
                        plane[y * width + x] = (ushort)Math.Clamp(v + offsets[bandTable[v >> shift]], 0, max);
                    }
                continue;
            }

            // Edge offset: the two neighbours along the class's direction (Table 8-13).
            var (dx, dy) = _saoBandOrClass[i] switch { 0 => (1, 0), 1 => (0, 1), 2 => (1, 1), _ => (-1, 1) };
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    int ax = x - dx, ay = y - dy, bx = x + dx, by = y + dy;
                    if (ax < 0 || ay < 0 || bx < 0 || by < 0 || ax >= width || bx >= width || ay >= height || by >= height) continue;
                    if (_bypass[Block(x << log2W, y << log2H)] != 0)
                    {
                        if (_statistics is not null) _statistics.SaoSamplesKeptForBypass++;
                        continue; // lossless: kept (8.7.3)
                    }
                    if (_sliceBoundariesMatter && (SaoNeighbourKept(x, y, ax, ay, log2W, log2H) || SaoNeighbourKept(x, y, bx, by, log2W, log2H)))
                        continue;
                    var v = source[y * width + x];
                    var edge = 2 + Math.Sign(v - source[ay * width + ax]) + Math.Sign(v - source[by * width + bx]);
                    // edgeIdx 0, 1, 2 become 1, 2, 0: a local minimum is category 1, a flat sample none.
                    if (edge <= 2) edge = edge == 2 ? 0 : edge + 1;
                    plane[y * width + x] = (ushort)Math.Clamp(v + offsets[edge], 0, max);
                }
            }
        }
    }
}
