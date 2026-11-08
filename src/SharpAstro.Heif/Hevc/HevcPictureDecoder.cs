namespace SharpAstro.Heif.Hevc;

/// <summary>
/// A decoded HEVC picture: one array of samples per component, each <c>[y * width + x]</c>, at the coded
/// size of the SPS (the conformance window and any HEIF crop are applied by whoever presents it).
/// </summary>
internal sealed record HevcPicture(
    int Width, int Height, int ChromaFormatIdc, int BitDepthLuma, int BitDepthChroma,
    ushort[] Luma, ushort[]? Cb, ushort[]? Cr, int ChromaWidth, int ChromaHeight,
    (int Left, int Right, int Top, int Bottom) ConformanceWindow,
    HevcVideoSignal? VideoSignal);

/// <summary>
/// What a decode exercised, for tests to check that a fixture covers what it is there for (a fixture
/// that never uses a mode cannot catch a bug in it; the Canon pictures, for one, never use planar).
/// Counted only when a collector is passed in.
/// </summary>
internal sealed class HevcDecodeStatistics
{
    /// <summary>Luma prediction blocks by mode.</summary>
    public long[] LumaModes { get; } = new long[35];

    /// <summary>Chroma prediction blocks by mode, after the 4:2:2 remap.</summary>
    public long[] ChromaModes { get; } = new long[35];

    /// <summary>Chroma coding units whose mode Table 8-3 changed.</summary>
    public long RemappedChromaModes { get; set; }

    /// <summary>Coded (cbf = 1) transform blocks by log2 size, luma and chroma together.</summary>
    public long[] CodedTransformSizes { get; } = new long[6];

    public long NxNUnits { get; set; }
    public long NonZeroQpDeltas { get; set; }
    public long StrongSmoothing { get; set; }

    /// <summary>Coded chroma blocks by qPi, the chroma QP before the 4:2:0 mapping of Table 8-10 (index qPi + 12).</summary>
    public long[] ChromaQpIndices { get; } = new long[70];
    public int Slices { get; set; }
}

/// <summary>
/// Decodes the slices of one intra picture (H.265 7.3.8 syntax, 8.4 and 8.6 decoding) into sample planes.
/// The envelope is checked by <see cref="HevcDecoder"/> before this is built; anything outside it that
/// can only be seen in slice data is refused where it appears.
/// </summary>
internal sealed class HevcPictureDecoder
{
    private readonly HevcSps _sps;
    private readonly HevcPps _pps;
    private readonly int _width, _height;
    private readonly int _chroma;            // ChromaArrayType
    private readonly int _log2SubW, _log2SubH;
    private readonly int _chromaWidth, _chromaHeight;
    private readonly int _log2Ctb, _widthInCtbs, _heightInCtbs;
    private readonly int _log2MinCb, _log2MinTb, _log2MaxTb;
    private readonly int _log2MinCuQpDeltaSize;
    private readonly int _qpBdOffsetY, _qpBdOffsetC;
    private readonly HevcScalingList? _scaling;

    private readonly ushort[] _luma;
    private readonly ushort[]? _cb, _cr;

    // Per CTB: the slice it belongs to (-1 until decoded).
    private readonly int[] _ctbSlice;

    // Per 4x4 luma block: coding quadtree depth, luma intra mode, QpY.
    private readonly int _width4;
    private readonly byte[] _ctDepth;
    private readonly byte[] _lumaMode;
    private readonly sbyte[] _qpMap;

    private int _slice = -1;
    private int _nextCtb;
    private HevcCabac _cabac = null!;

    // QP state (8.6.1).
    private int _sliceQpY, _cbOffset, _crOffset;
    private bool _isCuQpDeltaCoded;
    private int _cuQpDeltaVal;
    private int _qgPreviousQpY, _lastCuQpY, _qpYPred, _qpY;

    // Scratch.
    private readonly int[] _coefficients = new int[32 * 32];
    private readonly int[] _residual = new int[32 * 32];
    private readonly int[] _line = new int[4 * 32 + 1];
    private readonly bool[] _available = new bool[4 * 32 + 1];

    private readonly HevcDecodeStatistics? _statistics;

    public HevcPictureDecoder(HevcSps sps, HevcPps pps, HevcDecodeStatistics? statistics = null)
    {
        _statistics = statistics;
        _sps = sps;
        _pps = pps;
        _width = sps.Width;
        _height = sps.Height;
        _chroma = sps.ChromaFormatIdc;
        (_log2SubW, _log2SubH) = _chroma switch { 1 => (1, 1), 2 => (1, 0), _ => (0, 0) };
        _log2Ctb = sps.Log2CtbSize;
        _widthInCtbs = sps.WidthInCtbs;
        _heightInCtbs = sps.HeightInCtbs;
        _log2MinCb = sps.Log2MinCodingBlockSize;
        _log2MinTb = sps.Log2MinTransformBlockSize;
        _log2MaxTb = sps.Log2MaxTransformBlockSize;
        _log2MinCuQpDeltaSize = _log2Ctb - pps.DiffCuQpDeltaDepth;
        _qpBdOffsetY = 6 * (sps.BitDepthLuma - 8);
        _qpBdOffsetC = 6 * (sps.BitDepthChroma - 8);
        _scaling = sps.ScalingListEnabled ? pps.ScalingList ?? sps.ScalingList ?? HevcScalingList.Default : null;

        // 7.4.3.2.1: the picture is a whole number of minimum coding blocks.
        if ((_width & ((1 << _log2MinCb) - 1)) != 0 || (_height & ((1 << _log2MinCb) - 1)) != 0)
            throw new InvalidDataException($"The picture size {_width}x{_height} is not a multiple of the minimum coding block {1 << _log2MinCb}.");
        if (_pps.DiffCuQpDeltaDepth > _log2Ctb - _log2MinCb)
            throw new InvalidDataException($"diff_cu_qp_delta_depth {_pps.DiffCuQpDeltaDepth} is deeper than the coding quadtree.");

        _luma = new ushort[_width * _height];
        if (_chroma != 0)
        {
            _chromaWidth = _width >> _log2SubW;
            _chromaHeight = _height >> _log2SubH;
            _cb = new ushort[_chromaWidth * _chromaHeight];
            _cr = new ushort[_chromaWidth * _chromaHeight];
        }

        _ctbSlice = new int[_widthInCtbs * _heightInCtbs];
        Array.Fill(_ctbSlice, -1);
        _width4 = _width >> 2;
        var blocks = _width4 * (_height >> 2);
        _ctDepth = new byte[blocks];
        _lumaMode = new byte[blocks];
        _qpMap = new sbyte[blocks];
    }

    public HevcPicture Finish()
    {
        if (_nextCtb != _ctbSlice.Length)
            throw new InvalidDataException($"The picture's slices cover {_nextCtb} of its {_ctbSlice.Length} coding tree blocks.");
        return new HevcPicture(_width, _height, _chroma, _sps.BitDepthLuma, _sps.BitDepthChroma,
            _luma, _cb, _cr, _chromaWidth, _chromaHeight, _sps.ConformanceWindow, _sps.VideoSignal);
    }

    // ---- slices ------------------------------------------------------------------------------------

    public void DecodeSlice(HevcNalUnit nal)
    {
        var header = HevcSliceHeader.Parse(nal, _sps, _pps);
        if (header.FirstInPicture != (_slice < 0))
            throw new InvalidDataException(_slice < 0 ? "The picture's first slice is not marked first_slice_segment_in_pic." : "A second slice claims to be the first in the picture.");
        // Slices follow each other in CTB order with no gap and no overlap.
        if (header.SegmentAddress != _nextCtb)
            throw new InvalidDataException($"A slice starts at CTB {header.SegmentAddress}; the previous one ended before CTB {_nextCtb}.");

        _slice++;
        if (_statistics is not null) _statistics.Slices++;
        _sliceQpY = header.SliceQpY;
        _cbOffset = _pps.CbQpOffset + header.CbQpOffset;
        _crOffset = _pps.CrQpOffset + header.CrQpOffset;
        _lastCuQpY = _sliceQpY;          // the first quantization group of a slice predicts from SliceQpY
        _qgPreviousQpY = _sliceQpY;
        _isCuQpDeltaCoded = false;
        _cuQpDeltaVal = 0;

        _cabac = new HevcCabac(nal.Rbsp, header.DataOffset, nal.Rbsp.Length);
        _cabac.Start(_sliceQpY);

        var ctb = header.SegmentAddress;
        while (true)
        {
            if (ctb >= _ctbSlice.Length)
                throw new InvalidDataException("A slice runs past the last coding tree block without ending.");
            _ctbSlice[ctb] = _slice;
            var x = (ctb % _widthInCtbs) << _log2Ctb;
            var y = (ctb / _widthInCtbs) << _log2Ctb;
            CodingQuadtree(x, y, _log2Ctb, 0);
            ctb++;
            if (_cabac.Terminate() == 1) break; // end_of_slice_segment_flag
        }
        _nextCtb = ctb;
    }

    // ---- availability (6.4.1) ----------------------------------------------------------------------

    /// <summary>MinTbAddrZs (6.5.2) with no tiles: the CTB's raster address, then the z-order inside it.</summary>
    private int ZAddress(int x, int y)
    {
        var ctb = (y >> _log2Ctb) * _widthInCtbs + (x >> _log2Ctb);
        var mask = (1 << _log2Ctb) - 1;
        var bx = (x & mask) >> _log2MinTb;
        var by = (y & mask) >> _log2MinTb;
        var z = 0;
        for (var bit = 0; bit < _log2Ctb - _log2MinTb; bit++)
            z |= ((bx >> bit) & 1) << (2 * bit) | ((by >> bit) & 1) << (2 * bit + 1);
        return ctb << (2 * (_log2Ctb - _log2MinTb)) | z;
    }

    private int CtbAddress(int x, int y) => (y >> _log2Ctb) * _widthInCtbs + (x >> _log2Ctb);

    /// <summary>6.4.1: the neighbour is in the picture, earlier in z-scan order, and in the same slice.</summary>
    private bool Available(int xCurrent, int yCurrent, int xNeighbour, int yNeighbour)
    {
        if (xNeighbour < 0 || yNeighbour < 0 || xNeighbour >= _width || yNeighbour >= _height) return false;
        if (ZAddress(xNeighbour, yNeighbour) > ZAddress(xCurrent, yCurrent)) return false;
        return _ctbSlice[CtbAddress(xNeighbour, yNeighbour)] == _slice;
    }

    private int Block(int x, int y) => (y >> 2) * _width4 + (x >> 2);

    private void Fill(byte[] map, int x0, int y0, int size, byte value)
    {
        var w = Math.Min(size, _width - x0) >> 2;
        var h = Math.Min(size, _height - y0) >> 2;
        for (var j = 0; j < h; j++) map.AsSpan(Block(x0, y0 + 4 * j), w).Fill(value);
    }

    // ---- coding quadtree and coding unit (7.3.8.4, 7.3.8.5) ----------------------------------------

    private void CodingQuadtree(int x0, int y0, int log2Size, int depth)
    {
        var size = 1 << log2Size;
        bool split;
        if (x0 + size <= _width && y0 + size <= _height && log2Size > _log2MinCb)
        {
            // 9.3.4.2.2: one context per neighbour (left, above) that is deeper in the quadtree.
            var context = 0;
            if (Available(x0, y0, x0 - 1, y0) && _ctDepth[Block(x0 - 1, y0)] > depth) context++;
            if (Available(x0, y0, x0, y0 - 1) && _ctDepth[Block(x0, y0 - 1)] > depth) context++;
            split = _cabac.Decision(HevcContext.SplitCuFlag + context) == 1;
        }
        else
        {
            split = log2Size > _log2MinCb;
        }

        if (log2Size >= _log2MinCuQpDeltaSize)
        {
            // A new quantization group: its prediction falls back to the last CU of the previous group.
            _isCuQpDeltaCoded = false;
            _cuQpDeltaVal = 0;
            _qgPreviousQpY = _lastCuQpY;
        }

        if (split)
        {
            var half = size >> 1;
            int x1 = x0 + half, y1 = y0 + half;
            CodingQuadtree(x0, y0, log2Size - 1, depth + 1);
            if (x1 < _width) CodingQuadtree(x1, y0, log2Size - 1, depth + 1);
            if (y1 < _height) CodingQuadtree(x0, y1, log2Size - 1, depth + 1);
            if (x1 < _width && y1 < _height) CodingQuadtree(x1, y1, log2Size - 1, depth + 1);
        }
        else
        {
            CodingUnit(x0, y0, log2Size, depth);
        }
    }

    private void CodingUnit(int x0, int y0, int log2Size, int depth)
    {
        var size = 1 << log2Size;
        Fill(_ctDepth, x0, y0, size, (byte)depth);
        PredictQp(x0, y0);

        // part_mode: for an intra CU only at the minimum size, one bin, 1 = 2Nx2N and 0 = NxN.
        var intraSplit = log2Size == _log2MinCb && _cabac.Decision(HevcContext.PartMode) == 0;
        if (intraSplit && _statistics is not null) _statistics.NxNUnits++;

        // Intra luma modes: every prev_intra_luma_pred_flag first, then each mpm_idx / rem_intra_luma_pred_mode.
        var parts = intraSplit ? 4 : 1;
        var partSize = intraSplit ? size >> 1 : size;
        Span<bool> fromCandidates = stackalloc bool[4];
        for (var p = 0; p < parts; p++) fromCandidates[p] = _cabac.Decision(HevcContext.PrevIntraLumaPredFlag) == 1;
        for (var p = 0; p < parts; p++)
        {
            var xPb = x0 + (p & 1) * partSize;
            var yPb = y0 + (p >> 1) * partSize;
            int mode;
            if (fromCandidates[p])
            {
                var index = _cabac.Bypass() == 0 ? 0 : 1 + _cabac.Bypass(); // TR, cMax 2
                mode = LumaModeFromCandidates(xPb, yPb, index, -1);
            }
            else
            {
                mode = LumaModeFromCandidates(xPb, yPb, -1, _cabac.Bypass(5));
            }
            Fill(_lumaMode, xPb, yPb, partSize, (byte)mode);
        }

        var chromaMode = 0;
        if (_chroma != 0)
        {
            // intra_chroma_pred_mode: 0 -> 4 (derived from luma), else two bypass bins 0..3.
            var coded = _cabac.Decision(HevcContext.IntraChromaPredMode) == 0 ? 4 : _cabac.Bypass(2);
            chromaMode = ChromaMode(coded, _lumaMode[Block(x0, y0)]);
        }

        var maxDepth = _sps.MaxTransformHierarchyDepthIntra + (intraSplit ? 1 : 0);
        TransformTree(x0, y0, x0, y0, log2Size, 0, 0, maxDepth, intraSplit, 0, 0, chromaMode);

        var w = Math.Min(size, _width - x0) >> 2;
        var h = Math.Min(size, _height - y0) >> 2;
        for (var j = 0; j < h; j++) _qpMap.AsSpan(Block(x0, y0 + 4 * j), w).Fill((sbyte)_qpY);
        _lastCuQpY = _qpY;
    }

    /// <summary>8.4.2: the three most probable modes from the left and above neighbours, then the mode.</summary>
    private int LumaModeFromCandidates(int xPb, int yPb, int mpmIndex, int remainder)
    {
        var a = Available(xPb, yPb, xPb - 1, yPb) ? _lumaMode[Block(xPb - 1, yPb)] : HevcIntraPrediction.Dc;
        // Above is taken only from inside the same CTB row.
        var b = yPb - 1 >= ((yPb >> _log2Ctb) << _log2Ctb) && Available(xPb, yPb, xPb, yPb - 1)
            ? _lumaMode[Block(xPb, yPb - 1)]
            : HevcIntraPrediction.Dc;

        Span<int> candidates = stackalloc int[3];
        if (a == b)
        {
            if (a < 2)
            {
                candidates[0] = HevcIntraPrediction.Planar;
                candidates[1] = HevcIntraPrediction.Dc;
                candidates[2] = HevcIntraPrediction.Vertical;
            }
            else
            {
                candidates[0] = a;
                candidates[1] = 2 + ((a + 29) % 32);
                candidates[2] = 2 + ((a - 2 + 1) % 32);
            }
        }
        else
        {
            candidates[0] = a;
            candidates[1] = b;
            candidates[2] = a != HevcIntraPrediction.Planar && b != HevcIntraPrediction.Planar ? HevcIntraPrediction.Planar
                : a != HevcIntraPrediction.Dc && b != HevcIntraPrediction.Dc ? HevcIntraPrediction.Dc
                : HevcIntraPrediction.Vertical;
        }

        if (mpmIndex >= 0) return candidates[mpmIndex];

        candidates.Sort();
        var mode = remainder;
        for (var i = 0; i < 3; i++)
            if (mode >= candidates[i]) mode++;
        return mode;
    }

    /// <summary>
    /// Table 8-3: a 4:2:2 chroma block is half as wide as tall, so the derived angle is remapped to the
    /// chroma angle nearest the luma direction (a horizontal mode's slope doubles, a vertical mode's halves).
    /// <para>
    /// Entries 11 and 14 are 12 and 17. They were first transcribed as 11 and 16, which the Canon pictures
    /// cannot see (their chroma modes are 1, 10 and 26, which map to themselves) and the 4:2:0 fixtures
    /// cannot either; the x265 4:2:2 fixtures, on which ffmpeg and libde265 agree, each failed until both
    /// were corrected, and each correction alone left one of them failing. Both values are also what the
    /// nearest-angle geometry gives: mode 14 (-13, doubled to -26) is mode 17 exactly.
    /// </para>
    /// </summary>
    private static ReadOnlySpan<byte> Mode422 =>
    [
        0, 1, 2, 2, 2, 2, 3, 5, 7, 8, 10, 12, 13, 15, 17, 18, 19, 20,
        21, 22, 23, 23, 24, 24, 25, 25, 26, 27, 27, 28, 28, 29, 29, 30, 31,
    ];

    /// <summary>8.4.3: intra_chroma_pred_mode and the luma mode give the chroma mode.</summary>
    private int ChromaMode(int coded, int luma)
    {
        int mode = coded switch
        {
            0 => luma == 0 ? 34 : 0,
            1 => luma == 26 ? 34 : 26,
            2 => luma == 10 ? 34 : 10,
            3 => luma == 1 ? 34 : 1,
            _ => luma,
        };
        if (_chroma != 2) return mode;
        if (_statistics is not null && Mode422[mode] != mode) _statistics.RemappedChromaModes++;
        return Mode422[mode];
    }

    // ---- quantization parameters (8.6.1) -----------------------------------------------------------

    private void PredictQp(int xCb, int yCb)
    {
        var mask = (1 << _log2MinCuQpDeltaSize) - 1;
        int xQg = xCb - (xCb & mask), yQg = yCb - (yCb & mask);
        var ctb = CtbAddress(xCb, yCb);
        var left = Available(xCb, yCb, xQg - 1, yQg) && CtbAddress(xQg - 1, yQg) == ctb ? _qpMap[Block(xQg - 1, yQg)] : _qgPreviousQpY;
        var above = Available(xCb, yCb, xQg, yQg - 1) && CtbAddress(xQg, yQg - 1) == ctb ? _qpMap[Block(xQg, yQg - 1)] : _qgPreviousQpY;
        _qpYPred = (left + above + 1) >> 1;
        UpdateQp();
    }

    private void UpdateQp() =>
        _qpY = ((_qpYPred + _cuQpDeltaVal + 52 + 2 * _qpBdOffsetY) % (52 + _qpBdOffsetY)) - _qpBdOffsetY;

    private static ReadOnlySpan<byte> ChromaQp420 => [29, 30, 31, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37];

    /// <summary>Qp'Cb or Qp'Cr: Table 8-10 for 4:2:0, otherwise the identity clipped to 51.</summary>
    private int ChromaQp(int offset)
    {
        var qpi = Math.Clamp(_qpY + offset, -_qpBdOffsetC, 57);
        if (_statistics is not null) _statistics.ChromaQpIndices[qpi + 12]++;
        int qpc;
        if (_chroma == 1)
            qpc = qpi < 30 ? qpi : qpi > 43 ? qpi - 6 : ChromaQp420[qpi - 30];
        else
            qpc = Math.Min(qpi, 51);
        return qpc + _qpBdOffsetC;
    }

    // ---- transform tree and unit (7.3.8.8, 7.3.8.10) -----------------------------------------------

    /// <summary>
    /// <paramref name="parentCb"/> / <paramref name="parentCr"/> are the parent's chroma cbf flags (bit 0 the
    /// block, bit 1 the lower half of a 4:2:2 block), which a 4x4 luma block's chroma is coded with.
    /// </summary>
    private void TransformTree(int x0, int y0, int xBase, int yBase, int log2Size, int depth, int blockIndex,
        int maxDepth, bool intraSplit, int parentCb, int parentCr, int chromaMode)
    {
        bool split;
        if (log2Size <= _log2MaxTb && log2Size > _log2MinTb && depth < maxDepth && !(intraSplit && depth == 0))
            split = _cabac.Decision(HevcContext.SplitTransformFlag + 5 - log2Size) == 1;
        else
            split = log2Size > _log2MaxTb || (intraSplit && depth == 0);

        int cbfCb = 0, cbfCr = 0;
        if ((log2Size > 2 && _chroma != 0) || _chroma == 3)
        {
            var bothHalves = _chroma == 2 && (!split || log2Size == 3);
            if (depth == 0 || (parentCb & 1) != 0)
            {
                cbfCb = _cabac.Decision(HevcContext.CbfChroma + depth);
                if (bothHalves) cbfCb |= _cabac.Decision(HevcContext.CbfChroma + depth) << 1;
            }
            if (depth == 0 || (parentCr & 1) != 0)
            {
                cbfCr = _cabac.Decision(HevcContext.CbfChroma + depth);
                if (bothHalves) cbfCr |= _cabac.Decision(HevcContext.CbfChroma + depth) << 1;
            }
        }

        if (split)
        {
            var half = 1 << (log2Size - 1);
            int x1 = x0 + half, y1 = y0 + half;
            TransformTree(x0, y0, x0, y0, log2Size - 1, depth + 1, 0, maxDepth, intraSplit, cbfCb, cbfCr, chromaMode);
            TransformTree(x1, y0, x0, y0, log2Size - 1, depth + 1, 1, maxDepth, intraSplit, cbfCb, cbfCr, chromaMode);
            TransformTree(x0, y1, x0, y0, log2Size - 1, depth + 1, 2, maxDepth, intraSplit, cbfCb, cbfCr, chromaMode);
            TransformTree(x1, y1, x0, y0, log2Size - 1, depth + 1, 3, maxDepth, intraSplit, cbfCb, cbfCr, chromaMode);
            return;
        }

        // An intra transform unit always codes cbf_luma.
        var cbfLuma = _cabac.Decision(HevcContext.CbfLuma + (depth == 0 ? 1 : 0)) == 1;
        TransformUnit(x0, y0, xBase, yBase, log2Size, blockIndex, cbfLuma, cbfCb, cbfCr, parentCb, parentCr, chromaMode);
    }

    private void TransformUnit(int x0, int y0, int xBase, int yBase, int log2Size, int blockIndex,
        bool cbfLuma, int cbfCb, int cbfCr, int parentCb, int parentCr, int chromaMode)
    {
        // A 4x4 luma block's chroma (4:2:0 / 4:2:2) belongs to its 8x8 parent and is coded with the 4th block.
        var deferred = _chroma != 3 && log2Size == 2;
        var chromaCb = deferred ? parentCb : cbfCb;
        var chromaCr = deferred ? parentCr : cbfCr;
        var cbfChroma = _chroma != 0 && (chromaCb | chromaCr) != 0;

        if ((cbfLuma || cbfChroma) && _pps.CuQpDeltaEnabled && !_isCuQpDeltaCoded)
        {
            _cuQpDeltaVal = ReadCuQpDelta();
            if (_cuQpDeltaVal != 0 && _statistics is not null) _statistics.NonZeroQpDeltas++;
            _isCuQpDeltaCoded = true;
            UpdateQp();
        }

        var lumaMode = _lumaMode[Block(x0, y0)];
        Reconstruct(0, x0, y0, log2Size, lumaMode, cbfLuma);
        if (_chroma == 0) return;

        int xC, yC, log2C;
        if (!deferred)
        {
            log2C = _chroma == 3 ? log2Size : log2Size - 1;
            xC = x0 >> _log2SubW;
            yC = y0 >> _log2SubH;
        }
        else if (blockIndex == 3)
        {
            log2C = 2;
            xC = xBase >> _log2SubW;
            yC = yBase >> _log2SubH;
        }
        else
        {
            return;
        }

        var halves = _chroma == 2 ? 2 : 1;
        for (var t = 0; t < halves; t++)
            Reconstruct(1, xC, yC + (t << log2C), log2C, chromaMode, ((chromaCb >> t) & 1) != 0);
        for (var t = 0; t < halves; t++)
            Reconstruct(2, xC, yC + (t << log2C), log2C, chromaMode, ((chromaCr >> t) & 1) != 0);
    }

    /// <summary>cu_qp_delta_abs (prefix TU cMax 5 + EG0 suffix) and its sign; range-checked against 7.4.9.14.</summary>
    private int ReadCuQpDelta()
    {
        var prefix = 0;
        while (prefix < 5 && _cabac.Decision(HevcContext.CuQpDeltaAbs + (prefix == 0 ? 0 : 1)) == 1) prefix++;
        var value = prefix;
        if (prefix == 5) value += ExpGolombBypass(0);
        if (value != 0 && _cabac.Bypass() == 1) value = -value;
        var limit = 26 + _qpBdOffsetY / 2;
        if (value < -limit || value > limit - 1)
            throw new InvalidDataException($"CuQpDeltaVal {value} is outside [{-limit}, {limit - 1}].");
        return value;
    }

    /// <summary>k-th order Exp-Golomb in bypass bins (9.3.3.3), with the unary part capped.</summary>
    private int ExpGolombBypass(int k)
    {
        var value = 0;
        while (_cabac.Bypass() == 1)
        {
            value += 1 << k;
            if (++k > 20) throw new InvalidDataException("An Exp-Golomb bypass code is implausibly long.");
        }
        return value + _cabac.Bypass(k);
    }

    // ---- prediction and residual for one block -----------------------------------------------------

    private void Reconstruct(int component, int x, int y, int log2Size, int mode, bool coded)
    {
        var plane = component switch { 0 => _luma, 1 => _cb!, _ => _cr! };
        var stride = component == 0 ? _width : _chromaWidth;
        var bitDepth = component == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;

        Predict(component, plane, stride, x, y, log2Size, mode, bitDepth);
        if (_statistics is not null)
        {
            (component == 0 ? _statistics.LumaModes : _statistics.ChromaModes)[mode]++;
            if (coded) _statistics.CodedTransformSizes[log2Size]++;
        }
        if (!coded) return;

        var n = 1 << log2Size;
        var coefficients = _coefficients.AsSpan(0, n * n);
        coefficients.Clear();
        ResidualCoding(log2Size, component, ScanIndex(log2Size, component, mode), coefficients);

        var qp = component == 0 ? _qpY + _qpBdOffsetY : ChromaQp(component == 1 ? _cbOffset : _crOffset);
        HevcTransform.Scale(coefficients, log2Size, qp, bitDepth, _scaling?.Factors(log2Size, component));
        var residual = _residual.AsSpan(0, n * n);
        HevcTransform.Inverse(coefficients, residual, log2Size, dst: component == 0 && n == 4, bitDepth);

        var max = (1 << bitDepth) - 1;
        for (var j = 0; j < n; j++)
        {
            var row = (y + j) * stride + x;
            for (var i = 0; i < n; i++)
                plane[row + i] = (ushort)Math.Clamp(plane[row + i] + residual[j * n + i], 0, max);
        }
    }

    /// <summary>7.4.9.11: intra 4x4 (and 8x8 luma) blocks scan along the prediction direction.</summary>
    private int ScanIndex(int log2Size, int component, int mode)
    {
        if (log2Size == 2 || (log2Size == 3 && (component == 0 || _chroma == 3)))
        {
            if (mode is >= 6 and <= 14) return 2;
            if (mode is >= 22 and <= 30) return 1;
        }
        return 0;
    }

    /// <summary>8.4.4.2.1-2: gathers the reference samples with their availability, substitutes, and predicts.</summary>
    private void Predict(int component, ushort[] plane, int stride, int x, int y, int log2Size, int mode, int bitDepth)
    {
        var n = 1 << log2Size;
        var line = _line.AsSpan(0, 4 * n + 1);
        var available = _available.AsSpan(0, 4 * n + 1);
        int log2W = component == 0 ? 0 : _log2SubW, log2H = component == 0 ? 0 : _log2SubH;
        int xCurrent = x << log2W, yCurrent = y << log2H;
        var corner = 2 * n;

        // Left column, bottom (y = 2N - 1) up to the corner (y = -1).
        for (var k = 0; k <= corner; k++)
        {
            var yy = y + corner - 1 - k;
            var ok = Available(xCurrent, yCurrent, (x - 1) << log2W, yy << log2H);
            available[k] = ok;
            line[k] = ok ? plane[yy * stride + x - 1] : 0;
        }
        // Top row, x = 0 .. 2N - 1.
        for (var k = 0; k < corner; k++)
        {
            var xx = x + k;
            var ok = Available(xCurrent, yCurrent, xx << log2W, (y - 1) << log2H);
            available[corner + 1 + k] = ok;
            line[corner + 1 + k] = ok ? plane[(y - 1) * stride + xx] : 0;
        }

        HevcIntraPrediction.Substitute(line, available, bitDepth);
        var strong = HevcIntraPrediction.Predict(line, log2Size, mode,
            filterReferences: component == 0 || _chroma == 3,
            edgeFilters: component == 0,
            strongSmoothing: _sps.StrongIntraSmoothing && component == 0,
            bitDepth, plane, stride, x, y);
        if (strong && _statistics is not null) _statistics.StrongSmoothing++;
    }

    // ---- residual_coding (7.3.8.11) ----------------------------------------------------------------

    private static ReadOnlySpan<byte> SigContext4x4 => [0, 1, 4, 5, 2, 3, 4, 5, 6, 6, 8, 8, 7, 7, 8, 8];

    private void ResidualCoding(int log2Size, int component, int scanIdx, Span<int> coefficients)
    {
        var n = 1 << log2Size;
        var chroma = component > 0;

        // last_sig_coeff_{x,y}_prefix: truncated unary over contexts that depend on the block size.
        int ctxOffset, ctxShift;
        if (!chroma)
        {
            ctxOffset = 3 * (log2Size - 2) + ((log2Size - 1) >> 2);
            ctxShift = (log2Size + 1) >> 2;
        }
        else
        {
            ctxOffset = 15;
            ctxShift = log2Size - 2;
        }
        var cMax = (log2Size << 1) - 1;
        var xPrefix = 0;
        while (xPrefix < cMax && _cabac.Decision(HevcContext.LastXPrefix + ctxOffset + (xPrefix >> ctxShift)) == 1) xPrefix++;
        var yPrefix = 0;
        while (yPrefix < cMax && _cabac.Decision(HevcContext.LastYPrefix + ctxOffset + (yPrefix >> ctxShift)) == 1) yPrefix++;
        var lastX = LastPosition(xPrefix);
        var lastY = LastPosition(yPrefix);
        if (scanIdx == 2) (lastX, lastY) = (lastY, lastX);

        var log2Sb = log2Size - 2;
        var sbWidth = 1 << log2Sb;
        var subBlockOrder = HevcScan.Order(log2Sb, scanIdx);
        var positionOrder = HevcScan.Order(2, scanIdx);

        // Find the sub-block and position of the last significant coefficient in scan order.
        var lastSubBlock = (1 << (2 * log2Sb)) - 1;
        var lastScanPos = 16;
        int xC, yC;
        do
        {
            if (lastScanPos == 0)
            {
                lastScanPos = 16;
                if (--lastSubBlock < 0) throw new InvalidDataException("The last significant coefficient is outside its block.");
            }
            lastScanPos--;
            xC = ((subBlockOrder[lastSubBlock] & 0xFF) << 2) + (positionOrder[lastScanPos] & 0xFF);
            yC = ((subBlockOrder[lastSubBlock] >> 8) << 2) + (positionOrder[lastScanPos] >> 8);
        }
        while (xC != lastX || yC != lastY);

        Span<bool> codedSubBlock = stackalloc bool[64];
        Span<int> sigX = stackalloc int[16];
        Span<int> sigY = stackalloc int[16];
        Span<int> greater1 = stackalloc int[16];
        var firstSubBlock = true;
        var previousGreater1Ctx = 1;

        for (var i = lastSubBlock; i >= 0; i--)
        {
            var xS = subBlockOrder[i] & 0xFF;
            var yS = subBlockOrder[i] >> 8;
            var right = xS + 1 < sbWidth && codedSubBlock[yS * 8 + xS + 1];
            var below = yS + 1 < sbWidth && codedSubBlock[(yS + 1) * 8 + xS];

            var inferDc = false;
            bool coded;
            if (i < lastSubBlock && i > 0)
            {
                coded = _cabac.Decision(HevcContext.CodedSubBlockFlag + (right || below ? 1 : 0) + (chroma ? 2 : 0)) == 1;
                inferDc = true;
            }
            else
            {
                coded = true;
            }
            codedSubBlock[yS * 8 + xS] = coded;

            // Significance, in reverse scan order; the list holds the significant positions in that order.
            var count = 0;
            var start = 15;
            if (i == lastSubBlock)
            {
                sigX[0] = lastX;
                sigY[0] = lastY;
                count = 1;
                start = lastScanPos - 1;
            }
            if (coded)
            {
                var previousCsbf = (right ? 1 : 0) | (below ? 2 : 0);
                var sigContexts = SigContexts.AsSpan(SigContextRow(log2Size, chroma, scanIdx, previousCsbf, xS == 0 && yS == 0), 16);
                for (var p = start; p >= 0; p--)
                {
                    var xP = positionOrder[p] & 0xFF;
                    var yP = positionOrder[p] >> 8;
                    var x = (xS << 2) + xP;
                    var y = (yS << 2) + yP;
                    bool significant;
                    if (p > 0 || !inferDc)
                    {
                        significant = _cabac.Decision(HevcContext.SigCoeffFlag + sigContexts[(yP << 2) | xP]) == 1;
                        if (significant) inferDc = false;
                    }
                    else
                    {
                        significant = true; // the DC of a coded sub-block with nothing else significant
                    }
                    if (significant)
                    {
                        sigX[count] = x;
                        sigY[count] = y;
                        count++;
                    }
                }
            }
            if (count == 0) continue;

            // coeff_abs_level_greater1_flag for the first eight, greater2 for the first of those that is set.
            var ctxSet = i == 0 || chroma ? 0 : 2;
            if (!firstSubBlock && previousGreater1Ctx == 0) ctxSet++;
            firstSubBlock = false;
            var greater1Ctx = 1;
            var firstGreater1 = -1;
            var flagged = Math.Min(count, 8);
            for (var k = 0; k < flagged; k++)
            {
                var flag = _cabac.Decision(HevcContext.Greater1Flag + ctxSet * 4 + Math.Min(3, greater1Ctx) + (chroma ? 16 : 0));
                greater1[k] = flag;
                if (flag == 1)
                {
                    greater1Ctx = 0;
                    if (firstGreater1 < 0) firstGreater1 = k;
                }
                else if (greater1Ctx > 0)
                {
                    greater1Ctx++;
                }
            }
            previousGreater1Ctx = greater1Ctx;
            var greater2 = firstGreater1 >= 0 ? _cabac.Decision(HevcContext.Greater2Flag + ctxSet + (chroma ? 4 : 0)) : 0;

            // Signs (no sign hiding in the envelope), then the remaining levels.
            var signs = _cabac.Bypass(count);
            var rice = 0;
            for (var k = 0; k < count; k++)
            {
                var baseLevel = 1 + (k < 8 ? greater1[k] : 0) + (k == firstGreater1 ? greater2 : 0);
                var threshold = k < 8 ? (k == firstGreater1 ? 3 : 2) : 1;
                var level = baseLevel;
                if (baseLevel == threshold)
                {
                    level += Remaining(rice);
                    if (level > 3 * (1 << rice)) rice = Math.Min(rice + 1, 4);
                }
                var negative = ((signs >> (count - 1 - k)) & 1) != 0;
                if (level > 32768 || (level == 32768 && !negative))
                    throw new InvalidDataException($"A transform coefficient level of {(negative ? -level : level)} is outside 16 bits.");
                coefficients[sigY[k] * n + sigX[k]] = negative ? -level : level;
            }
        }
    }

    private int LastPosition(int prefix) =>
        prefix <= 3 ? prefix : ((1 << ((prefix >> 1) - 1)) * (2 + (prefix & 1))) + _cabac.Bypass((prefix >> 1) - 1);

    /// <summary>
    /// <see cref="SigContext"/> for every case, built from it: the context depends on the block size, the
    /// component, the scan, the coded-sub-block flags to the right and below, whether this is the first
    /// sub-block, and the position inside the sub-block, and on nothing else. A row is 16 contexts indexed
    /// <c>yP * 4 + xP</c>; <see cref="SigContextRow"/> finds it.
    /// </summary>
    private static readonly byte[] SigContexts = BuildSigContexts();

    private static int SigContextRow(int log2Size, bool chroma, int scanIdx, int previousCsbf, bool firstSubBlock) =>
        (((((log2Size - 2) * 2 + (chroma ? 1 : 0)) * 3 + scanIdx) * 4 + previousCsbf) * 2 + (firstSubBlock ? 0 : 1)) * 16;

    private static byte[] BuildSigContexts()
    {
        var table = new byte[4 * 2 * 3 * 4 * 2 * 16];
        for (var log2Size = 2; log2Size <= 5; log2Size++)
            foreach (var chroma in new[] { false, true })
                for (var scanIdx = 0; scanIdx < 3; scanIdx++)
                    for (var csbf = 0; csbf < 4; csbf++)
                        foreach (var first in new[] { true, false })
                        {
                            // Any sub-block other than the first stands for all of them: only xS > 0 || yS > 0 matters.
                            int xS = 0, yS = first ? 0 : 1;
                            if (!first && log2Size == 2) continue; // a 4x4 block is one sub-block
                            var row = SigContextRow(log2Size, chroma, scanIdx, csbf, first);
                            for (var yP = 0; yP < 4; yP++)
                                for (var xP = 0; xP < 4; xP++)
                                    table[row + (yP << 2) + xP] = (byte)SigContext(log2Size, chroma, scanIdx, (xS << 2) + xP, (yS << 2) + yP, xS, yS, csbf);
                        }
        return table;
    }

    /// <summary>9.3.4.2.5: sig_coeff_flag's context.</summary>
    private static int SigContext(int log2Size, bool chroma, int scanIdx, int x, int y, int xS, int yS, int previousCsbf)
    {
        int sigCtx;
        if (log2Size == 2)
        {
            sigCtx = SigContext4x4[(y << 2) + x];
        }
        else if (x + y == 0)
        {
            sigCtx = 0;
        }
        else
        {
            int xP = x & 3, yP = y & 3;
            sigCtx = previousCsbf switch
            {
                0 => xP + yP == 0 ? 2 : xP + yP < 3 ? 1 : 0,
                1 => yP == 0 ? 2 : yP == 1 ? 1 : 0,
                2 => xP == 0 ? 2 : xP == 1 ? 1 : 0,
                _ => 2,
            };
            if (!chroma)
            {
                if (xS > 0 || yS > 0) sigCtx += 3;
                sigCtx += log2Size == 3 ? (scanIdx == 0 ? 9 : 15) : 21;
            }
            else
            {
                sigCtx += log2Size == 3 ? 9 : 12;
            }
        }
        return chroma ? 27 + sigCtx : sigCtx;
    }

    /// <summary>coeff_abs_level_remaining (9.3.3.11): a Rice prefix, then Exp-Golomb past a prefix of 4.</summary>
    private int Remaining(int rice)
    {
        var prefix = 0;
        while (_cabac.Bypass() == 1)
            if (++prefix > 20) throw new InvalidDataException("A coeff_abs_level_remaining prefix is implausibly long.");
        if (prefix <= 3) return (prefix << rice) + _cabac.Bypass(rice);
        return (((1 << (prefix - 3)) + 3 - 1) << rice) + _cabac.Bypass(prefix - 3 + rice);
    }
}
