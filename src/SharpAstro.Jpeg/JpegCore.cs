using System.Buffers;
using System.Numerics;

namespace SharpAstro.Jpeg;

/// <summary>
/// The decoder engine: marker parsing, Huffman/entropy decode (baseline +
/// progressive), restart intervals, per-component sample strips, and the
/// upsample + colour-convert pass that feeds a row sink. The full-scale pipeline is a 1:1 port of
/// stb_image's JPEG decoder, so output is byte-exact against StbImageSharp. That
/// port (<c>StbImage.Generated.Jpg.cs</c>) is no longer in this repo — the
/// guarantee now lives as a committed golden digest baseline
/// (<c>Fixtures/jpeg-oracle-golden.tsv</c>, driven by <c>JpegDecoderOracleTests</c>).
///
/// <para><b>Strips, not planes.</b> stb decodes every block into whole-image component planes and only then
/// upsamples and colour-converts. Here the IDCT writes one <i>unit</i> at a time — an MCU row of an interleaved
/// scan, a block row of a single-component scan — into a per-component strip a few rows tall, and every output
/// row whose source rows are complete is assembled and handed to the <see cref="IJpegRowSink"/> straight away.
/// The strip keeps the plane rows the upsampler still needs from the unit above (its "far" row), so the
/// output is the same bytes stb produces, from memory that depends on the image's width and not its height.
/// The assembly keeps stb's per-component row state machine (<c>YStep</c>/<c>Line0</c>/<c>Line1</c>) verbatim;
/// only the rows it points at now live in a strip. Three cases cannot stream and keep whole planes, sized
/// exactly: baseline scans that are not one full scan (several non-interleaved scans), a stream whose
/// colour markers change after the scan, and the restart after either of those is found out late.
/// Progressive streams keep their coefficients whole-image (every scan is needed) and stream the IDCT from
/// those at the end, so their sample planes never exist either.</para>
///
/// Departures from the reference:
///
/// <list type="bullet">
/// <item>Component strips, progressive coefficient buffers, and line buffers are
/// rented from <see cref="ArrayPool{T}"/> instead of malloc'd — and zeroed, so
/// strip padding is deterministic where stb reads malloc garbage (valid streams
/// never observe either).</item>
/// <item>A scale denominator of 2/4/8 swaps the 8×8 IDCT for the reduced kernel,
/// shrinking planes and output by the square of the factor (the entropy layout —
/// MCU geometry, block counts, coefficient buffers — stays full-resolution, as
/// the bitstream dictates).</item>
/// <item>APP0 JFIF detection follows upstream stb C (<c>"JFIF\0"</c>); the
/// machine-transpiled port reuses the Adobe tag there by mistake, which only
/// diverges for files carrying both JFIF and an Adobe transform-0 marker.</item>
/// <item>Errors throw <see cref="InvalidDataException"/> instead of returning
/// null — except unknown/corrupt markers after the first scan, where stb (and we)
/// salvage what has been decoded.</item>
/// </list>
/// </summary>
internal ref struct JpegCore
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    // --- entropy bit reader (port of stb's code_buffer machinery) ---
    private uint _codeBuffer;
    private int _codeBits;
    private byte _marker = 0xff;
    private bool _nomore;

    // --- frame state ---
    internal int ImgX;
    internal int ImgY;
    internal int ImgN;
    private int _imgHMax, _imgVMax;
    private int _imgMcuX, _imgMcuY, _imgMcuW, _imgMcuH;
    private bool _progressive;
    private bool _jfif;
    private int _app14ColorTransform = -1;
    private int _rgbCount;
    private int _restartInterval;
    private int _todo;
    private int _eobRun;

    // --- current scan ---
    private int _scanN;
    private readonly int[] _order = new int[4];
    private int _specStart, _specEnd, _succHigh, _succLow;

    // --- tables ---
    private readonly JpegHuffman[] _huffDc = { new(), new(), new(), new() };
    private readonly JpegHuffman[] _huffAc = { new(), new(), new(), new() };
    private readonly ushort[][] _dequant = { new ushort[64], new ushort[64], new ushort[64], new ushort[64] };
    private readonly short[][] _fastAc = { new short[512], new short[512], new short[512], new short[512] };

    private readonly Component[] _comp = { new(), new(), new(), new() };

    // --- scaled decode ---
    private readonly int _scale;     // 1, 2, 4, 8
    private readonly int _blockSize; // 8 / _scale — IDCT output tile edge

    // --- strip pipeline ---

    /// <summary>
    /// Extra plane rows a strip holds on top of one unit. After a unit the upsampler's lowest row still
    /// to be read trails the newest decoded row by at most two (a component whose next output row sits one
    /// short of its unit boundary because another component's kernel blends a far row), so a unit plus
    /// two retained rows always fits. <c>BeginUnit</c> checks it rather than trusting this comment.
    /// </summary>
    private const int StripMargin = 2;

    private bool _usePlanes;         // whole-image planes instead of strips (forced, or the fallback cases)
    private bool _streaming;          // the scan being decoded streams through strips
    private bool _streamedScan;       // a scan has streamed already (to the end or to a salvaged early end)
    private bool _stripsReady, _planesReady, _pipelineReady;
    private bool _streamJfif;         // colour markers when the streamed scan began; see DecodeBody
    private int _streamApp14;
    private bool _isRgb;
    private int _nextOut;             // next output row to assemble
    private int _totalUnits;          // MCU rows (interleaved) or block rows (single component)
    private int _unitsPushed;         // units whose samples are in the strips
    private long _peakPlaneBytes;
    private readonly (byte[] Arr, int Off)[] _rows = new (byte[], int)[4];

    // --- region decode (DecodeRegionTo) ---
    //
    // A full decode is the region that happens to be the whole image: every one of these has its full-image value
    // until SetRegion narrows it, so the assembly below has a single code path and the full decode does not branch.

    private bool _region;
    private int _outEnd;              // one past the last output row to assemble: OutY, or the region's end
    private int _cropX, _cropW;       // output columns assembled per row: [0, OutX), or the region's
    private int _unitStart, _unitEnd; // units that get IDCT and assembly: [0, _totalUnits), or the ones the region reads
    private int _entropyUnits, _transformedUnits;

    internal int OutX { get; private set; }
    internal int OutY { get; private set; }

    /// <summary>What this decode held and which engine ran it (see <see cref="JpegDecodeStats"/>).</summary>
    internal JpegDecodeStats Stats => new(_peakPlaneBytes, _usePlanes, false, _streamedScan ? _entropyUnits : _totalUnits, _transformedUnits);

    public JpegCore(ReadOnlySpan<byte> data, int scale, bool wholePlanes = false)
    {
        _data = data;
        _scale = scale;
        _blockSize = 8 / scale;
        _usePlanes = wholePlanes;
    }

    private sealed class Component
    {
        public int Id, H, V, Tq, Hd, Ha;
        public int DcPred;
        public int X, Y;             // full-resolution sample dimensions
        public int W2, H2;           // scaled plane stride / height
        public byte[]? Data;         // strip (a few unit rows) or, in the plane fallback, the whole plane (W2 * H2)
        public bool DataPooled;      // Data came from ArrayPool (strips); whole planes are exact-size arrays
        public int StripBase;        // plane row held at Data's row 0 (always 0 for whole planes)
        public int StripRows;        // Data's capacity in plane rows
        public int Stride;           // Data's row length: W2, or (for a region's strip) just the block columns it reads
        public int ColBase;          // plane column held at Data's column 0 (0 unless a region's strip starts further in)
        public int Avail;            // plane rows decoded so far, counted from row 0; int.MaxValue once complete
        public int UnitBlockRows;    // block rows per unit: V for interleaved scans, 1 for a single component
        public short[]? Coeff;       // pooled coefficient store (progressive only)
        public int CoeffW, CoeffH;   // in 8×8 blocks (full resolution)
        public byte[]? LineBuf;      // pooled upsample row buffer (components that are not 1:1)

        // per-decode resample state (mirrors stb's stbi__resample)
        public int Hs, Vs, YStep, YPos, WLores, YsScaled;
        public int Line0, Line1;     // plane ROW indices (stb keeps offsets); the strip maps them to Data
        public int ResampleKind;
        public bool UsesFar;         // the kernel blends the far row as well as the near one

        // The columns the upsampler is run over, in this component's own samples: [WinA, WinA + WinN). The whole
        // row for a full decode; for a region, its columns plus the neighbour the horizontal kernels read beyond
        // each interior edge. CropOff is where the region's first pixel sits in the kernel's output.
        public int WinA, WinN, CropOff;

        // Block columns that are IDCT'd: everything, or just the ones that hold the window.
        public int BlkLo, BlkHi;
    }

    private enum ScanMode
    {
        Load,
        Header,
    }

    // ============================== public surface ==============================

    /// <summary>Parses SOI → SOF only; allocates nothing, decodes no entropy data.</summary>
    public JpegInfo ParseInfo()
    {
        DecodeHeader(ScanMode.Header);

        // Header-scan mode returns before the frame header computes the sampling
        // maxima, so derive subsampling directly from the component factors here.
        var hMax = 1;
        var vMax = 1;
        for (var i = 0; i < ImgN; ++i)
        {
            hMax = Math.Max(hMax, _comp[i].H);
            vMax = Math.Max(vMax, _comp[i].V);
        }

        var subsampled = false;
        for (var i = 0; i < ImgN; ++i)
        {
            if (_comp[i].H != hMax || _comp[i].V != vMax)
                subsampled = true;
        }

        return new JpegInfo(ImgX, ImgY, ImgN, _progressive) { ChromaSubsampled = subsampled };
    }

    /// <summary>
    /// Decodes <paramref name="data"/> and streams every output row to <paramref name="sink"/>, returning
    /// what the decode held. Owns the engine's lifetime: the buffers are returned on every path, including
    /// exceptions, and a stream that cannot be decoded a few rows at a time is decoded again with whole planes.
    /// </summary>
    /// <param name="forceWholePlanes">Diagnostic: run the whole-plane engine, which is stb's pipeline as it was
    /// before strips. Tests use it as the reference the strip pipeline has to reproduce byte for byte.</param>
    /// <param name="region">Decode only this rectangle of the output (see <see cref="SetRegion"/>); the sink then
    /// receives only its rows, <c>region.Width</c> pixels wide.</param>
    internal static JpegDecodeStats Run<TSink>(ReadOnlySpan<byte> data, int scale, ref TSink sink, bool forceWholePlanes = false, JpegRegion? region = null)
        where TSink : IJpegRowSink, allows ref struct
    {
        var core = new JpegCore(data, scale, forceWholePlanes);
        long peak;
        try
        {
            core.ReadHeader();
            if (region is { } r)
                core.SetRegion(r);
            sink.Begin(core.OutX, core.OutY);
            if (core.DecodeBody(ref sink))
                return core.Stats;

            peak = core._peakPlaneBytes;
        }
        finally
        {
            core.ReturnBuffers();
        }

        // The stream would not stream (see DecodeBody). The sink has some of its rows already; the whole-plane
        // engine delivers all of them again from row 0.
        var again = new JpegCore(data, scale, wholePlanes: true);
        try
        {
            again.ReadHeader();
            if (region is { } r2)
                again.SetRegion(r2);
            again.DecodeBody(ref sink);
            var stats = again.Stats;
            return stats with { PeakPlaneBytes = Math.Max(peak, stats.PeakPlaneBytes), Restarted = true };
        }
        finally
        {
            again.ReturnBuffers();
        }
    }

    /// <summary>
    /// Parses SOI → SOF and sets up the frame geometry (<see cref="OutX"/>, <see cref="OutY"/>, the MCU grid
    /// and the progressive coefficient stores). Decodes no entropy data and holds no sample memory yet.
    /// </summary>
    public void ReadHeader()
    {
        _restartInterval = 0;
        DecodeHeader(ScanMode.Load);
    }

    /// <summary>
    /// Narrows the decode to a rectangle of the output (pixels at this decode's scale). Call after
    /// <see cref="ReadHeader"/>, before <see cref="DecodeBody"/>. Only the region's rows and columns are assembled
    /// and handed to the sink, which is asked for rows of <c>w * 4</c> bytes; everything else this class does for a
    /// full decode it does for the region, bit for bit (the row state machine is seeded where a full decode would
    /// have it at the region's first row, and the upsamplers see their real image edges only at real image edges).
    /// What decode work is left out is described at <see cref="PlanRows"/>, <see cref="PlanColumns"/> and
    /// <see cref="StreamScan"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The region is empty or not entirely inside the output.</exception>
    public void SetRegion(JpegRegion region)
    {
        var (x, y, w, h) = (region.X, region.Y, region.Width, region.Height);
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x > OutX - w || y > OutY - h)
        {
            throw new ArgumentException(
                $"Region ({x}, {y}) {w}×{h} is empty or not inside the {OutX}×{OutY} image at this scale.", nameof(region));
        }

        _region = true;
        _cropX = x;
        _cropW = w;
        _nextOut = y;
        _outEnd = y + h;
    }

    /// <summary>
    /// Decodes the scans and streams every output row to <paramref name="sink"/>. Returns false when the stream
    /// cannot be decoded a few rows at a time and must be decoded again with whole planes
    /// (<c>new JpegCore(data, scale, wholePlanes: true)</c>); the rows already delivered are then overwritten.
    /// That is the case when
    /// <list type="bullet">
    /// <item>a scan follows the one that streamed: today's decoder lets a later scan overwrite the blocks of an
    /// earlier one, and rows already delivered cannot be taken back, or</item>
    /// <item>an Adobe or JFIF marker after the scan changed the colour model: today's decoder converts with the
    /// state at the end of the stream, and the rows streamed used the state at the start of the scan.</item>
    /// </list>
    /// Neither occurs in a valid file; both are cheap to detect and are the only reasons for a restart.
    /// </summary>
    public bool DecodeBody<TSink>(ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        if (!DecodeJpegImage(ref sink))
            return false;

        if (_streamedScan && (_jfif != _streamJfif || _app14ColorTransform != _streamApp14))
            return false;

        CompleteImage(ref sink);
        return true;
    }

    /// <summary>
    /// Makes sure every output row has been delivered. Reached after the markers are exhausted, which for a
    /// salvaged stream can be before any scan, or before a progressive stream's <see cref="Finish"/>: the planes
    /// such a decode leaves are zero, and the same zero rows go through the same assembly as everything else.
    /// </summary>
    private void CompleteImage<TSink>(ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        if (_usePlanes)
        {
            EnsurePlanes(); // a stream with no scan at all still converts (zero) planes
            EmitAllRows(ref sink);
        }
        else if (_nextOut < _outEnd)
        {
            EnsureStrips();
            FlushZeroUnits(_unitsPushed, ref sink);
        }
    }

    // ============================== sample memory ==============================

    /// <summary>
    /// Per-component resample state, once. This is the setup stb's <c>stbi__resample</c> does before its row loop.
    /// </summary>
    private void PreparePipeline()
    {
        if (_pipelineReady)
            return;

        _pipelineReady = true;
        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            c.Hs = _imgHMax / c.H;
            c.Vs = _imgVMax / c.V;
            c.YStep = c.Vs >> 1;
            c.WLores = (OutX + c.Hs - 1) / c.Hs;
            c.YPos = 0;
            c.Line0 = c.Line1 = 0;
            c.YsScaled = (c.Y + _scale - 1) / _scale;
            c.ResampleKind =
                c.Hs == 1 && c.Vs == 1 ? JpegResample.OneToOne :
                c.Hs == 1 && c.Vs == 2 ? JpegResample.V2 :
                c.Hs == 2 && c.Vs == 1 ? JpegResample.H2 :
                c.Hs == 2 && c.Vs == 2 ? JpegResample.Hv2 :
                JpegResample.Generic;
            c.UsesFar = c.ResampleKind == JpegResample.V2 || c.ResampleKind == JpegResample.Hv2;
            PlanColumns(c);

            // The 1:1 kernel aliases the plane row instead of copying it, so only the others need a buffer.
            if (c.ResampleKind != JpegResample.OneToOne)
                c.LineBuf = ArrayPool<byte>.Shared.Rent(_region ? c.WinN * c.Hs + 3 : OutX + 3);
        }

        _isRgb = ComputeIsRgb();
        if (_region)
            PlanRows();
    }

    /// <summary>
    /// Which of the component's columns the upsampler runs over, and which block columns that needs. A full decode
    /// runs it over the whole row, which is the only place its kernels meet the image's own edges.
    ///
    /// <para>A region's window is its columns widened by one sample on each side wherever the kernel reads a
    /// horizontal neighbour (H2 and Hv2), and clipped to the row. The kernels treat the first and last sample they
    /// are given as the image edge, and their output for those two samples is the edge's, not the interior's. So the
    /// widening is what makes an <i>interior</i> window edge harmless: the samples it spoils are the margin, which
    /// is cropped away, and at a real image edge there is no margin and the kernel's edge rule is the right one.
    /// This is the kernels themselves, run unchanged, on a span of the row; no second copy of their arithmetic.</para>
    /// </summary>
    private void PlanColumns(Component c)
    {
        if (!_region)
        {
            c.WinA = 0;
            c.WinN = c.WLores;
            c.CropOff = 0;
            c.BlkLo = 0;
            c.BlkHi = int.MaxValue;
            return;
        }

        var first = _cropX / c.Hs;
        var last = (_cropX + _cropW - 1) / c.Hs;
        var margin = c.ResampleKind == JpegResample.H2 || c.ResampleKind == JpegResample.Hv2;
        var a = margin ? Math.Max(0, first - 1) : first;
        var b = margin ? Math.Min(c.WLores, last + 2) : last + 1; // exclusive; last < WLores
        c.WinA = a;
        c.WinN = b - a;
        c.CropOff = _cropX - a * c.Hs;
        c.BlkLo = a / _blockSize;
        c.BlkHi = (b - 1) / _blockSize;
    }

    /// <summary>
    /// A region's rows. Puts each component's row state where a full decode has it when it reaches the region's
    /// first row (<see cref="SeedRows"/>), then works out which units that and the region's last row read, which
    /// is where the margin the vertical upsamplers need comes from: an output row on a unit boundary blends a
    /// plane row of the unit above (or below) whenever the kernel reads a far row.
    /// </summary>
    private void PlanRows()
    {
        var first = _nextOut;
        var last = _outEnd - 1;
        var u0 = int.MaxValue;
        var u1 = 0;
        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            var rowsPerUnit = c.UnitBlockRows * _blockSize;
            SeedRows(c, first);
            u0 = Math.Min(u0, PlaneRowsRead(c).Lowest / rowsPerUnit);
            SeedRows(c, last);
            u1 = Math.Max(u1, PlaneRowsRead(c).Deepest / rowsPerUnit);
            SeedRows(c, first);
        }

        _unitStart = u0;
        _unitEnd = Math.Min(u1, _totalUnits - 1) + 1;
        _unitsPushed = u0;
    }

    /// <summary>
    /// Sets the component's row state to what stb's per-row loop leaves it at after <paramref name="j"/> rows,
    /// in closed form (the loop has no data dependence, so it can be jumped). <c>YStep</c> starts at
    /// <c>Vs/2</c> and wraps at <c>Vs</c>; each wrap moves <c>YPos</c> on and, until the plane's last row, moves
    /// <c>Line1</c> down one with <c>Line0</c> trailing it. <c>RegionDecodeTests</c> pins this against the loop itself
    /// by comparing every region with the matching crop of a full decode.
    /// </summary>
    private static void SeedRows(Component c, int j)
    {
        var total = (long)(c.Vs >> 1) + j;
        var wraps = (int)(total / c.Vs);
        c.YStep = (int)(total % c.Vs);
        c.YPos = wraps;
        c.Line1 = Math.Min(wraps, c.YsScaled - 1);
        c.Line0 = wraps == 0 ? 0 : Math.Min(wraps - 1, c.YsScaled - 1);
    }

    /// <summary>
    /// The shallowest and deepest plane rows the next output row reads, given the row state. A kernel that blends
    /// a far row reads both of <c>Line0</c>/<c>Line1</c>; one that does not reads only the near row, which is
    /// <c>Line1</c> in the lower half of a vertical expansion and <c>Line0</c> in the upper (see <see cref="EmitRow"/>).
    /// </summary>
    private static (int Lowest, int Deepest) PlaneRowsRead(Component c)
    {
        var yBot = c.YStep >= (c.Vs >> 1);
        var near = yBot ? c.Line1 : c.Line0;
        return (c.UsesFar ? c.Line0 : near, (c.UsesFar || yBot) ? c.Line1 : c.Line0);
    }

    private bool ComputeIsRgb() => ImgN == 3 && (_rgbCount == 3 || (_app14ColorTransform == 0 && !_jfif));

    /// <summary>
    /// Rents each component's strip: one unit of rows plus <see cref="StripMargin"/>, zeroed. The size is a
    /// function of the image's width and sampling and nothing else, a few hundred KB for a 5100-wide scan.
    /// The pool's power-of-two rounding can at worst double that, which is the cost of a second strip. It is
    /// not the 64 MiB rent a 33,660,000-byte whole plane costs (issue #28).
    /// </summary>
    private void EnsureStrips()
    {
        if (_stripsReady)
            return;

        _stripsReady = true;
        PreparePipeline();
        _streamJfif = _jfif;
        _streamApp14 = _app14ColorTransform;

        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            c.StripRows = c.UnitBlockRows * _blockSize + StripMargin;

            // A region's strip holds only the block columns it reads, and starts at the first unit it reads: the
            // plane rows and columns above and left of that are never written, and never read.
            c.ColBase = c.BlkLo * _blockSize;
            c.Stride = _region ? (c.BlkHi - c.BlkLo + 1) * _blockSize : c.W2;
            var length = c.StripRows * c.Stride;
            c.Data = ArrayPool<byte>.Shared.Rent(length);
            c.DataPooled = true;
            // Zeroed as the whole plane was, so what a salvaged stream never writes is deterministic.
            c.Data.AsSpan(0, length).Clear();
            c.StripBase = _unitStart * c.UnitBlockRows * _blockSize;
            c.Avail = c.StripBase;
            _peakPlaneBytes += c.Data.Length;
        }
    }

    /// <summary>
    /// Whole-image planes, for the cases that cannot stream (see <see cref="DecodeBody"/>). They are allocated at
    /// their exact size rather than rented: <c>ArrayPool&lt;byte&gt;.Shared</c> rounds a rent up to the next
    /// power of two, and a plane just over one (a 5100×6600 scan's 33,660,000 bytes) rented a 64 MiB array.
    /// A new array is zeroed by the runtime, as the rented planes were cleared.
    /// </summary>
    private void EnsurePlanes()
    {
        if (_planesReady)
            return;

        _planesReady = true;
        PreparePipeline();
        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            var length = c.W2 * c.H2; // ≤ int.MaxValue, checked with the frame header
            c.Data = new byte[length];
            c.DataPooled = false;
            c.StripBase = 0;
            c.Stride = c.W2; // whole planes, even for a region: only the assembly is cropped
            c.ColBase = 0;
            c.StripRows = c.H2;
            c.Avail = int.MaxValue;
            _peakPlaneBytes += length;
        }
    }

    /// <summary>Returns all pooled buffers. Safe to call multiple times.</summary>
    public void ReturnBuffers()
    {
        for (var i = 0; i < 4; ++i)
        {
            var c = _comp[i];
            if (c.Data != null)
            {
                if (c.DataPooled)
                    ArrayPool<byte>.Shared.Return(c.Data);
                c.Data = null;
            }

            if (c.Coeff != null)
            {
                ArrayPool<short>.Shared.Return(c.Coeff);
                c.Coeff = null;
            }

            if (c.LineBuf != null)
            {
                ArrayPool<byte>.Shared.Return(c.LineBuf);
                c.LineBuf = null;
            }
        }
    }

    // ============================== strips: unit bookkeeping ==============================

    /// <summary>
    /// Before a unit's samples are written: slides each strip down so the plane rows the upsampler has not
    /// finished with sit at its top, and the unit lands after them. <c>Line0</c> is the lowest row any
    /// remaining output row can read (<c>Line1</c> only ever moves up from it), so everything under it is dead.
    /// </summary>
    private void BeginUnit()
    {
        ++_transformedUnits;
        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            var low = Math.Min(c.Line0, c.Avail);
            if (low > c.StripBase)
            {
                var keep = c.Avail - low;
                if (keep > 0)
                    c.Data.AsSpan((low - c.StripBase) * c.Stride, keep * c.Stride).CopyTo(c.Data); // memmove: overlap is fine
                c.StripBase = low;
            }

            if (c.Avail - c.StripBase + c.UnitBlockRows * _blockSize > c.StripRows)
                throw new InvalidOperationException("JPEG: strip window overflow (decoder bug)");
        }
    }

    /// <summary>
    /// After a unit's samples are written: marks them available and assembles every output row they complete.
    /// The last unit marks everything available, because nothing will be written after it, and the rows that
    /// the upsampler clamps to the plane's last row are then ready.
    /// </summary>
    private void EndUnit<TSink>(int unit, ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        var last = unit == _totalUnits - 1;
        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            c.Avail = last ? int.MaxValue : (unit + 1) * c.UnitBlockRows * _blockSize;
        }

        _unitsPushed = unit + 1;
        EmitReadyRows(ref sink);
    }

    /// <summary>
    /// Zeroes plane columns <paramref name="fromCol"/>.. of the strip rows <paramref name="unit"/> occupies (the
    /// part of that range the strip holds: a region's strip starts at <c>ColBase</c>).
    /// </summary>
    private void ClearUnitColumns(Component c, int unit, int fromCol)
    {
        var from = Math.Clamp(fromCol - c.ColBase, 0, c.Stride);
        var rows = c.UnitBlockRows * _blockSize;
        var first = unit * rows - c.StripBase;
        for (var r = 0; r < rows; ++r)
            c.Data.AsSpan((first + r) * c.Stride + from, c.Stride - from).Clear();
    }

    /// <summary>
    /// Pushes all-zero units through the assembly from <paramref name="from"/> on. A zero plane is what today's
    /// decoder leaves wherever a scan did not reach, so a salvaged stream (restart interval run out, no scan,
    /// a bad marker, a progressive stream that never finished) converts the same zeros, blended with the rows
    /// above exactly as the whole planes were.
    /// </summary>
    private void FlushZeroUnits<TSink>(int from, ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        for (var u = from; u < _unitEnd; ++u)
        {
            BeginUnit();
            for (var k = 0; k < ImgN; ++k)
                ClearUnitColumns(_comp[k], u, 0);
            EndUnit(u, ref sink);
        }
    }

    // ============================== assembly ==============================

    /// <summary>
    /// Assembles every output row whose source rows are decoded. Whether the next row is ready is a function of
    /// stb's row state: the deepest plane row it reads is <c>Line1</c> (the far row, for kernels that blend
    /// it, and for the near row whenever it is the lower of the pair) or <c>Line0</c>.
    /// </summary>
    private void EmitReadyRows<TSink>(ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        while (_nextOut < _outEnd && NextRowIsReady())
        {
            EmitRow(_nextOut, ref sink);
            ++_nextOut;
        }
    }

    private bool NextRowIsReady()
    {
        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            var need = (c.UsesFar || c.YStep >= (c.Vs >> 1)) ? c.Line1 : c.Line0;
            if (need >= c.Avail)
                return false;
        }

        return true;
    }

    /// <summary>The whole-plane engine: every plane is complete, so every row is ready.</summary>
    private void EmitAllRows<TSink>(ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        // Converted with the colour markers as they stand at the end of the stream, as stb does.
        _isRgb = ComputeIsRgb();
        while (_nextOut < _outEnd)
        {
            EmitRow(_nextOut, ref sink);
            ++_nextOut;
        }
    }

    /// <summary>
    /// Upsamples and colour-converts output row <paramref name="j"/> into the sink: stb's per-row loop body,
    /// unchanged except that <c>Line0</c>/<c>Line1</c> are plane rows and the strip maps them to offsets, and that
    /// the columns are the window <see cref="PlanColumns"/> chose (all of them, for a full decode) cropped to the
    /// <c>_cropW</c> output pixels the sink wants.
    /// </summary>
    private void EmitRow<TSink>(int j, ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        // Current output-resolution row per component: (array, offset) because the
        // 1:1 kernel aliases the plane directly instead of copying.
        var rows = _rows;
        var decodeN = ImgN;
        var width = _cropW;

        for (var k = 0; k < decodeN; ++k)
        {
            var c = _comp[k];
            var yBot = c.YStep >= (c.Vs >> 1);
            var inNear = ((yBot ? c.Line1 : c.Line0) - c.StripBase) * c.Stride;
            var inFar = ((yBot ? c.Line0 : c.Line1) - c.StripBase) * c.Stride;
            if (c.ResampleKind == JpegResample.OneToOne)
            {
                rows[k] = (c.Data!, inNear + (_cropX - c.ColBase));
            }
            else
            {
                var shift = c.WinA - c.ColBase;
                JpegResample.Row(c.ResampleKind, c.LineBuf, c.Data, inNear + shift, inFar + shift, c.WinN, c.Hs);
                rows[k] = (c.LineBuf!, c.CropOff);
            }

            if (++c.YStep >= c.Vs)
            {
                c.YStep = 0;
                c.Line0 = c.Line1;
                if (++c.YPos < c.YsScaled)
                    ++c.Line1;
            }
        }

        var outRow = sink.RowBuffer(j);
        var y = rows[0].Arr.AsSpan(rows[0].Off);

        if (ImgN == 3)
        {
            if (_isRgb)
            {
                var g = rows[1].Arr.AsSpan(rows[1].Off);
                var b = rows[2].Arr.AsSpan(rows[2].Off);
                for (var i = 0; i < width; ++i)
                {
                    outRow[i * 4 + 0] = y[i];
                    outRow[i * 4 + 1] = g[i];
                    outRow[i * 4 + 2] = b[i];
                    outRow[i * 4 + 3] = 255;
                }
            }
            else
            {
                JpegColor.YCbCrToRgbaRow(outRow, y, rows[1].Arr.AsSpan(rows[1].Off), rows[2].Arr.AsSpan(rows[2].Off), width);
            }
        }
        else if (ImgN == 4)
        {
            var c1 = rows[1].Arr.AsSpan(rows[1].Off);
            var c2 = rows[2].Arr.AsSpan(rows[2].Off);
            var c3 = rows[3].Arr.AsSpan(rows[3].Off);
            if (_app14ColorTransform == 0)
            {
                // Plain Adobe CMYK: fold K into each ink channel.
                for (var i = 0; i < width; ++i)
                {
                    var m = c3[i];
                    outRow[i * 4 + 0] = JpegColor.Blinn8x8(y[i], m);
                    outRow[i * 4 + 1] = JpegColor.Blinn8x8(c1[i], m);
                    outRow[i * 4 + 2] = JpegColor.Blinn8x8(c2[i], m);
                    outRow[i * 4 + 3] = 255;
                }
            }
            else if (_app14ColorTransform == 2)
            {
                // YCCK: YCbCr → RGB, invert to CMY, then fold K.
                JpegColor.YCbCrToRgbaRow(outRow, y, c1, c2, width);
                for (var i = 0; i < width; ++i)
                {
                    var m = c3[i];
                    outRow[i * 4 + 0] = JpegColor.Blinn8x8((byte)(255 - outRow[i * 4 + 0]), m);
                    outRow[i * 4 + 1] = JpegColor.Blinn8x8((byte)(255 - outRow[i * 4 + 1]), m);
                    outRow[i * 4 + 2] = JpegColor.Blinn8x8((byte)(255 - outRow[i * 4 + 2]), m);
                }
            }
            else
            {
                // No (or unrecognised) transform tag — stb treats it as YCbCr.
                JpegColor.YCbCrToRgbaRow(outRow, y, c1, c2, width);
            }
        }
        else // grayscale
        {
            for (var i = 0; i < width; ++i)
            {
                var v = y[i];
                outRow[i * 4 + 0] = v;
                outRow[i * 4 + 1] = v;
                outRow[i * 4 + 2] = v;
                outRow[i * 4 + 3] = 255;
            }
        }

        sink.RowDone(j);
    }

    // ============================== byte reader ==============================

    private bool AtEof => _pos >= _data.Length;

    private byte Get8() => _pos < _data.Length ? _data[_pos++] : (byte)0;

    private int Get16()
    {
        int hi = Get8();
        return (hi << 8) | Get8();
    }

    private void Skip(int n) => _pos = Math.Min(_data.Length, _pos + n);

    // ============================== bit reader ==============================

    private static readonly uint[] Bmask =
        { 0, 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535 };

    private static readonly int[] Jbias =
        { 0, -1, -3, -7, -15, -31, -63, -127, -255, -511, -1023, -2047, -4095, -8191, -16383, -32767 };

    private static readonly byte[] Dezigzag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7,
        14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
        // padding so a run can overshoot to k=79 without bounds failure, as in stb
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    };

    private void GrowBuffer()
    {
        do
        {
            var b = _nomore ? 0u : Get8();
            if (b == 0xff)
            {
                int c = Get8();
                while (c == 0xff)
                    c = Get8();

                if (c != 0)
                {
                    // A real marker terminates entropy data; remember it and feed
                    // zero bits from here on (stb's "nomore" mechanism).
                    _marker = (byte)c;
                    _nomore = true;
                    return;
                }
                // 0xFF00 is a stuffed literal 0xFF byte — fall through with b = 0xff.
            }

            _codeBuffer |= b << (24 - _codeBits);
            _codeBits += 8;
        } while (_codeBits <= 24);
    }

    private int HuffDecode(JpegHuffman h)
    {
        if (_codeBits < 16)
            GrowBuffer();

        var c = (int)((_codeBuffer >> (32 - JpegHuffman.FastBits)) & ((1 << JpegHuffman.FastBits) - 1));
        int k = h.Fast[c];
        if (k < 255)
        {
            int s = h.Size[k];
            if (s > _codeBits)
                return -1;
            _codeBuffer <<= s;
            _codeBits -= s;
            return h.Values[k];
        }

        // Slow path: walk the canonical maxcode ladder above FastBits.
        var temp = _codeBuffer >> 16;
        for (k = JpegHuffman.FastBits + 1; ; ++k)
            if (temp < h.MaxCode[k])
                break;

        if (k == 17)
        {
            _codeBits -= 16;
            return -1;
        }

        if (k > _codeBits)
            return -1;
        c = (int)(((_codeBuffer >> (32 - k)) & Bmask[k]) + h.Delta[k]);
        if (c < 0 || c >= 256)
            return -1;
        _codeBits -= k;
        _codeBuffer <<= k;
        return h.Values[c];
    }

    /// <summary>Receives n magnitude bits and sign-extends per the JPEG EXTEND procedure.</summary>
    private int ExtendReceive(int n)
    {
        if (_codeBits < n)
            GrowBuffer();
        if (_codeBits < n)
            return 0; // ran out of bits in a corrupt/truncated stream

        var sgn = (int)(_codeBuffer >> 31);
        var k = BitOperations.RotateLeft(_codeBuffer, n);
        _codeBuffer = k & ~Bmask[n];
        k &= Bmask[n];
        _codeBits -= n;
        return (int)k + (Jbias[n] & (sgn - 1));
    }

    private int GetBits(int n)
    {
        if (_codeBits < n)
            GrowBuffer();
        if (_codeBits < n)
            return 0;

        var k = BitOperations.RotateLeft(_codeBuffer, n);
        _codeBuffer = k & ~Bmask[n];
        k &= Bmask[n];
        _codeBits -= n;
        return (int)k;
    }

    private bool GetBit()
    {
        if (_codeBits < 1)
            GrowBuffer();
        if (_codeBits < 1)
            return false;

        var k = _codeBuffer;
        _codeBuffer <<= 1;
        --_codeBits;
        return (k & 0x80000000) != 0;
    }

    // ============================== block decode ==============================

    private void DecodeBlock(scoped Span<short> data, JpegHuffman hdc, JpegHuffman hac, short[] fac, int b, ushort[] dequant)
    {
        if (_codeBits < 16)
            GrowBuffer();
        var t = HuffDecode(hdc);
        if (t < 0 || t > 15)
            throw Err("bad huffman code");

        data.Clear();
        var diff = t != 0 ? ExtendReceive(t) : 0;
        if (!AddIntsValid(_comp[b].DcPred, diff))
            throw Err("bad delta");
        var dc = _comp[b].DcPred + diff;
        _comp[b].DcPred = dc;
        if (!Mul2ShortsValid(dc, dequant[0]))
            throw Err("can't merge dc and ac");
        data[0] = (short)(dc * dequant[0]);

        var k = 1;
        do
        {
            if (_codeBits < 16)
                GrowBuffer();

            // Fast-AC: run length, value, and code length resolved in one lookup
            // when everything fits in the 9-bit window.
            var c = (int)((_codeBuffer >> (32 - JpegHuffman.FastBits)) & ((1 << JpegHuffman.FastBits) - 1));
            int r = fac[c];
            int s;
            if (r != 0)
            {
                k += (r >> 4) & 15;
                s = r & 15;
                if (s > _codeBits)
                    throw Err("bad huffman code");
                _codeBuffer <<= s;
                _codeBits -= s;
                var zig = Dezigzag[k++];
                data[zig] = (short)((r >> 8) * dequant[zig]);
            }
            else
            {
                var rs = HuffDecode(hac);
                if (rs < 0)
                    throw Err("bad huffman code");
                s = rs & 15;
                r = rs >> 4;
                if (s == 0)
                {
                    if (rs != 0xf0)
                        break; // EOB
                    k += 16;   // ZRL
                }
                else
                {
                    k += r;
                    var zig = Dezigzag[k++];
                    data[zig] = (short)(ExtendReceive(s) * dequant[zig]);
                }
            }
        } while (k < 64);
    }

    private void DecodeBlockProgDc(scoped Span<short> data, JpegHuffman hdc, int b)
    {
        if (_specEnd != 0)
            throw Err("can't merge dc and ac");
        if (_codeBits < 16)
            GrowBuffer();

        if (_succHigh == 0)
        {
            // First DC scan: full magnitude at succ_low precision.
            data.Clear();
            var t = HuffDecode(hdc);
            if (t < 0 || t > 15)
                throw Err("can't merge dc and ac");
            var diff = t != 0 ? ExtendReceive(t) : 0;
            if (!AddIntsValid(_comp[b].DcPred, diff))
                throw Err("bad delta");
            var dc = _comp[b].DcPred + diff;
            _comp[b].DcPred = dc;
            if (!Mul2ShortsValid(dc, 1 << _succLow))
                throw Err("can't merge dc and ac");
            data[0] = (short)(dc * (1 << _succLow));
        }
        else
        {
            // DC refinement: one bit per block.
            if (GetBit())
                data[0] = (short)(data[0] + (1 << _succLow));
        }
    }

    private void DecodeBlockProgAc(scoped Span<short> data, JpegHuffman hac, short[] fac)
    {
        if (_specStart == 0)
            throw Err("can't merge dc and ac");

        if (_succHigh == 0)
        {
            // First AC scan for this band.
            var shift = _succLow;
            if (_eobRun != 0)
            {
                --_eobRun;
                return;
            }

            var k = _specStart;
            do
            {
                if (_codeBits < 16)
                    GrowBuffer();
                var c = (int)((_codeBuffer >> (32 - JpegHuffman.FastBits)) & ((1 << JpegHuffman.FastBits) - 1));
                int r = fac[c];
                int s;
                if (r != 0)
                {
                    k += (r >> 4) & 15;
                    s = r & 15;
                    if (s > _codeBits)
                        throw Err("bad huffman code");
                    _codeBuffer <<= s;
                    _codeBits -= s;
                    var zig = Dezigzag[k++];
                    data[zig] = (short)((r >> 8) * (1 << shift));
                }
                else
                {
                    var rs = HuffDecode(hac);
                    if (rs < 0)
                        throw Err("bad huffman code");
                    s = rs & 15;
                    r = rs >> 4;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            _eobRun = 1 << r;
                            if (r != 0)
                                _eobRun += GetBits(r);
                            --_eobRun;
                            break;
                        }

                        k += 16;
                    }
                    else
                    {
                        k += r;
                        var zig = Dezigzag[k++];
                        data[zig] = (short)(ExtendReceive(s) * (1 << shift));
                    }
                }
            } while (k <= _specEnd);
        }
        else
        {
            // AC refinement: correction bits for already-nonzero coefficients,
            // newly-significant coefficients placed after `r` zero-history slots.
            var bit = (short)(1 << _succLow);

            if (_eobRun != 0)
            {
                --_eobRun;
                for (var k = _specStart; k <= _specEnd; ++k)
                {
                    ref var p = ref data[Dezigzag[k]];
                    if (p != 0 && GetBit() && (p & bit) == 0)
                        p = p > 0 ? (short)(p + bit) : (short)(p - bit);
                }
            }
            else
            {
                var k = _specStart;
                do
                {
                    var rs = HuffDecode(hac);
                    if (rs < 0)
                        throw Err("bad huffman code");
                    var s = rs & 15;
                    var r = rs >> 4;
                    var newCoeff = 0;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            _eobRun = (1 << r) - 1;
                            if (r != 0)
                                _eobRun += GetBits(r);
                            r = 64; // never reaches the newCoeff write below
                        }
                    }
                    else
                    {
                        if (s != 1)
                            throw Err("bad huffman code");
                        newCoeff = GetBit() ? bit : -bit;
                    }

                    while (k <= _specEnd)
                    {
                        ref var p = ref data[Dezigzag[k++]];
                        if (p != 0)
                        {
                            if (GetBit() && (p & bit) == 0)
                                p = p > 0 ? (short)(p + bit) : (short)(p - bit);
                        }
                        else
                        {
                            if (r == 0)
                            {
                                p = (short)newCoeff;
                                break;
                            }

                            --r;
                        }
                    }
                } while (k <= _specEnd);
            }
        }
    }

    // ============================== scan orchestration ==============================

    private byte GetMarker()
    {
        byte x;
        if (_marker != 0xff)
        {
            x = _marker;
            _marker = 0xff;
            return x;
        }

        x = Get8();
        if (x != 0xff)
            return 0xff;
        while (x == 0xff)
            x = Get8(); // consume fill bytes (returns 0 at EOF, exiting the loop)

        return x;
    }

    private void Reset()
    {
        _codeBits = 0;
        _codeBuffer = 0;
        _nomore = false;
        _comp[0].DcPred = _comp[1].DcPred = _comp[2].DcPred = _comp[3].DcPred = 0;
        _marker = 0xff;
        _todo = _restartInterval != 0 ? _restartInterval : 0x7fffffff;
        _eobRun = 0;
    }

    /// <summary>
    /// Restart-interval bookkeeping after each MCU (or each block in
    /// non-interleaved scans). Returns false when the scan is over — either the
    /// interval expired without an RSTn marker (next marker belongs to the
    /// caller) or the stream ended.
    /// </summary>
    private bool CheckRestart()
    {
        if (--_todo > 0)
            return true;

        if (_codeBits < 24)
            GrowBuffer();
        if (!(_marker >= 0xd0 && _marker <= 0xd7))
            return false;
        Reset();
        return true;
    }

    private void ParseEntropyCodedData()
    {
        Reset();
        if (!_progressive)
        {
            Span<short> data = stackalloc short[64];
            if (_scanN == 1)
            {
                // Non-interleaved: this component's own block grid, no MCU padding.
                var n = _order[0];
                var c = _comp[n];
                var w = (c.X + 7) >> 3;
                var h = (c.Y + 7) >> 3;
                for (var j = 0; j < h; ++j)
                {
                    for (var i = 0; i < w; ++i)
                    {
                        DecodeBlock(data, _huffDc[c.Hd], _huffAc[c.Ha], _fastAc[c.Ha], n, _dequant[c.Tq]);
                        IdctToPlane(c, i, j, data);
                        if (!CheckRestart())
                            return;
                    }
                }
            }
            else
            {
                // Interleaved: full MCU sweep, h×v blocks per component per MCU.
                for (var j = 0; j < _imgMcuY; ++j)
                {
                    for (var i = 0; i < _imgMcuX; ++i)
                    {
                        for (var k = 0; k < _scanN; ++k)
                        {
                            var n = _order[k];
                            var c = _comp[n];
                            for (var y = 0; y < c.V; ++y)
                            {
                                for (var x = 0; x < c.H; ++x)
                                {
                                    DecodeBlock(data, _huffDc[c.Hd], _huffAc[c.Ha], _fastAc[c.Ha], n, _dequant[c.Tq]);
                                    IdctToPlane(c, i * c.H + x, j * c.V + y, data);
                                }
                            }
                        }

                        if (!CheckRestart())
                            return;
                    }
                }
            }
        }
        else
        {
            if (_scanN == 1)
            {
                var n = _order[0];
                var c = _comp[n];
                var w = (c.X + 7) >> 3;
                var h = (c.Y + 7) >> 3;
                for (var j = 0; j < h; ++j)
                {
                    for (var i = 0; i < w; ++i)
                    {
                        var coeff = c.Coeff.AsSpan(64 * (i + j * c.CoeffW), 64);
                        if (_specStart == 0)
                            DecodeBlockProgDc(coeff, _huffDc[c.Hd], n);
                        else
                            DecodeBlockProgAc(coeff, _huffAc[c.Ha], _fastAc[c.Ha]);
                        if (!CheckRestart())
                            return;
                    }
                }
            }
            else
            {
                // Interleaved progressive scans carry DC only (spec: AC scans are
                // always single-component).
                for (var j = 0; j < _imgMcuY; ++j)
                {
                    for (var i = 0; i < _imgMcuX; ++i)
                    {
                        for (var k = 0; k < _scanN; ++k)
                        {
                            var n = _order[k];
                            var c = _comp[n];
                            for (var y = 0; y < c.V; ++y)
                            {
                                for (var x = 0; x < c.H; ++x)
                                {
                                    var x2 = i * c.H + x;
                                    var y2 = j * c.V + y;
                                    var coeff = c.Coeff.AsSpan(64 * (x2 + y2 * c.CoeffW), 64);
                                    DecodeBlockProgDc(coeff, _huffDc[c.Hd], n);
                                }
                            }
                        }

                        if (!CheckRestart())
                            return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Baseline, one scan that carries every component (interleaved, or the only component): entropy-decode a
    /// unit, IDCT it into the strips, assemble what that completes, and go on. Same block order, restart handling
    /// and salvage as <see cref="ParseEntropyCodedData"/>; the planes it would leave are what the strips hold at the
    /// time, and what a scan never reaches is zero rows.
    /// </summary>
    private void StreamScan<TSink>(ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        // A single component is scanned on its own block grid (no MCU padding) and restarts count blocks; otherwise
        // an MCU is the interleaved h×v blocks of every component. A unit is one row of either.
        var single = ImgN == 1;
        var mcusPerUnit = single ? (_comp[_order[0]].X + 7) >> 3 : _imgMcuX;

        // What a full decode does, narrowed to a region: entropy-decode from the unit the region starts in (found
        // through the restart markers, when there are any) to the one it ends in, and IDCT and assemble only the
        // units from _unitStart on. For a full decode _unitStart is 0, _unitEnd the frame's, and the seek is a no-op.
        var firstUnit = 0;
        var firstMcu = 0;
        if (_region && _restartInterval != 0)
            SeekToRestartInterval(mcusPerUnit, out firstUnit, out firstMcu);

        Reset();
        Span<short> data = stackalloc short[64];
        for (var j = firstUnit; j < _unitEnd; ++j)
        {
            // Units above the region are decoded only to keep the bit stream in step: no strip rows, no IDCT.
            var emit = j >= _unitStart;
            if (emit)
                BeginUnit();
            ++_entropyUnits;

            for (var i = j == firstUnit ? firstMcu : 0; i < mcusPerUnit; ++i)
            {
                if (single)
                {
                    var n = _order[0];
                    var c = _comp[n];
                    DecodeBlock(data, _huffDc[c.Hd], _huffAc[c.Ha], _fastAc[c.Ha], n, _dequant[c.Tq]);
                    if (emit && InColumns(c, i))
                        IdctToPlane(c, i, j, data);
                }
                else
                {
                    for (var k = 0; k < _scanN; ++k)
                    {
                        var n = _order[k];
                        var c = _comp[n];
                        for (var y = 0; y < c.V; ++y)
                        {
                            for (var x = 0; x < c.H; ++x)
                            {
                                DecodeBlock(data, _huffDc[c.Hd], _huffAc[c.Ha], _fastAc[c.Ha], n, _dequant[c.Tq]);
                                var bx = i * c.H + x;
                                if (emit && InColumns(c, bx))
                                    IdctToPlane(c, bx, j * c.V + y, data);
                            }
                        }
                    }
                }

                if (!CheckRestart())
                {
                    EndScanEarly(j, i + 1, ref sink);
                    return;
                }
            }

            if (emit)
                EndUnit(j, ref sink);
        }

        // A region stops here, mid-stream: the rest of the scan, and whatever follows it, is not read. The marker
        // loop that called this ends the decode when it finds the entropy data where a marker should be.
    }

    /// <summary>
    /// The scan stopped after the last whole MCU (block) of <paramref name="unit"/>'s first
    /// <paramref name="doneMcus"/>: its restart interval ran out without an RSTn, or the stream ended there. That is also how
    /// every scan with a restart interval that divides the image ends (the last interval's RSTn never comes), where
    /// nothing is left to zero. What the scan did not decode is zero in today's planes: the rest of this unit's
    /// rows, and every unit below. (A region only has the units it reads to zero, and none at all when the scan
    /// stopped before reaching them.)
    /// </summary>
    private void EndScanEarly<TSink>(int unit, int doneMcus, ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        if (unit >= _unitStart)
        {
            for (var k = 0; k < ImgN; ++k)
            {
                var c = _comp[k];
                var doneBlockCols = ImgN == 1 ? doneMcus : doneMcus * c.H;
                ClearUnitColumns(c, unit, Math.Min(doneBlockCols * _blockSize, c.W2));
            }

            EndUnit(unit, ref sink);
        }

        FlushZeroUnits(Math.Max(unit + 1, _unitStart), ref sink);
    }

    /// <summary>
    /// Moves the byte position to the start of the restart interval that contains the first MCU a region needs,
    /// so that the intervals before it are never entropy-decoded. Interval <c>r</c> starts after the <c>r</c>th
    /// RSTn marker, with its bit reader and DC predictors reset, which is exactly the state <see cref="Reset"/> sets;
    /// so decoding from there is the same decode the full one does from that point on.
    ///
    /// <para>The markers are counted, not trusted: each must be the next one in the RST0..RST7 cycle, and the scan
    /// must not end (EOI, any other marker, end of data) before the interval is reached. If the sequence does not
    /// check out, nothing moves and the decode starts from the top of the scan, as a full decode does, which is
    /// also what reproduces a damaged stream's salvage exactly. What is not checked is the size of the intervals
    /// that are skipped: the full decode takes DRI MCUs per interval whatever the data holds, and a stream whose
    /// intervals do not hold that many (corrupt, not just truncated) is one a skip cannot tell from a good one.</para>
    /// </summary>
    /// <param name="unit">The unit the decode starts in: at most the region's first.</param>
    /// <param name="mcu">The MCU of that unit it starts at; 0 unless the interval starts mid-row.</param>
    private void SeekToRestartInterval(int mcusPerUnit, out int unit, out int mcu)
    {
        unit = 0;
        mcu = 0;
        var interval = (int)((long)_unitStart * mcusPerUnit / _restartInterval);
        if (interval == 0 || !TryFindRestartInterval(interval, out var at))
            return;

        _pos = at;
        var start = (long)interval * _restartInterval; // the first MCU of that interval; <= the region's first
        unit = (int)(start / mcusPerUnit);
        mcu = (int)(start % mcusPerUnit);
    }

    /// <summary>
    /// The byte position just after the <paramref name="interval"/>th RSTn marker of the entropy-coded data at
    /// the current position (so where interval <paramref name="interval"/> begins), or false when the data does not
    /// have that many markers in sequence. A marker is <c>0xFF</c> then a code that is neither <c>0x00</c> (a stuffed
    /// data byte) nor <c>0xFF</c> (fill, which may precede the code); entropy-coded data holds no other <c>0xFF</c>.
    /// </summary>
    private bool TryFindRestartInterval(int interval, out int position)
    {
        position = 0;
        var p = _pos;
        for (var r = 0; r < interval; ++r)
        {
            while (true)
            {
                var ff = _data.Slice(p).IndexOf((byte)0xFF);
                if (ff < 0 || p + ff + 1 >= _data.Length)
                    return false;

                p += ff;
                var code = _data[p + 1];
                if (code == 0x00 || code == 0xFF)
                {
                    p += code == 0x00 ? 2 : 1; // a stuffed byte; or a fill byte, the next 0xFF may start the marker
                    continue;
                }

                if (code != 0xD0 + (r & 7))
                    return false; // EOI, another marker, or an RSTn out of sequence

                p += 2;
                break;
            }
        }

        position = p;
        return true;
    }

    /// <summary>
    /// Whether the scan just parsed carries every component exactly once, so that it alone fills the planes and
    /// can stream. A single-component frame qualifies on its own scan. A scan that names a component twice, or
    /// leaves one out, would leave a plane at zero in a way whole planes reproduce trivially, so it keeps them.
    /// </summary>
    private bool ScanCoversEveryComponent()
    {
        if (ImgN == 1)
            return _scanN == 1;
        if (_scanN != ImgN)
            return false;

        var seen = 0;
        for (var i = 0; i < _scanN; ++i)
            seen |= 1 << _order[i];
        return seen == (1 << ImgN) - 1;
    }

    /// <summary>Progressive only: dequantize the accumulated coefficients and IDCT every block, a unit at a time.</summary>
    private void FinishStreamed<TSink>(ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        EnsureStrips();
        Span<short> block = stackalloc short[64];
        for (var u = _unitStart; u < _unitEnd; ++u)
        {
            BeginUnit();
            for (var n = 0; n < ImgN; ++n)
            {
                var c = _comp[n];
                var dequant = _dequant[c.Tq];
                var w = (c.X + 7) >> 3;
                var h = (c.Y + 7) >> 3;

                // Blocks beyond the component's own grid are never IDCT'd, so their samples are zero, as in a
                // zeroed plane. The strip holds the last unit's samples there until cleared.
                ClearUnitColumns(c, u, 0);
                for (var y = 0; y < c.UnitBlockRows; ++y)
                {
                    var by = u * c.UnitBlockRows + y;
                    if (by >= h)
                        break;
                    // Only the block columns the strip holds (all of them, for a full decode).
                    for (var bx = c.BlkLo; bx < w && bx <= c.BlkHi; ++bx)
                    {
                        var coeff = c.Coeff.AsSpan(64 * (bx + by * c.CoeffW), 64);
                        for (var k = 0; k < 64; ++k)
                            block[k] = (short)(coeff[k] * dequant[k]);
                        IdctToPlane(c, bx, by, block);
                    }
                }
            }

            EndUnit(u, ref sink);
        }
    }

    /// <summary>
    /// Progressive only, whole-plane engine: dequantize the accumulated coefficients and IDCT every block into
    /// the whole planes. This is stb's <c>stbi__jpeg_finish</c>, kept as the reference the streamed
    /// <see cref="FinishStreamed"/> must reproduce.
    /// </summary>
    private void FinishWholePlanes()
    {
        Span<short> block = stackalloc short[64];
        for (var n = 0; n < ImgN; ++n)
        {
            var c = _comp[n];
            var dequant = _dequant[c.Tq];
            var w = (c.X + 7) >> 3;
            var h = (c.Y + 7) >> 3;
            for (var j = 0; j < h; ++j)
            {
                for (var i = 0; i < w; ++i)
                {
                    var coeff = c.Coeff.AsSpan(64 * (i + j * c.CoeffW), 64);
                    for (var k = 0; k < 64; ++k)
                        block[k] = (short)(coeff[k] * dequant[k]);
                    IdctToPlane(c, i, j, block);
                }
            }
        }
    }

    /// <summary>
    /// Writes block (<paramref name="bx"/>, <paramref name="by"/>) of the component's block grid into its plane
    /// rows, which sit at <c>StripBase</c> and below in the strip (the whole plane has <c>StripBase</c> 0).
    /// </summary>
    private void IdctToPlane(Component c, int bx, int by, scoped ReadOnlySpan<short> data)
    {
        var off = (by * _blockSize - c.StripBase) * c.Stride + (bx * _blockSize - c.ColBase);
        if (_blockSize == 8)
            JpegIdct.Idct8x8(c.Data!, off, c.Stride, data);
        else
            JpegIdct.IdctReduced(c.Data!, off, c.Stride, data, _blockSize);
    }

    /// <summary>Whether block column <paramref name="bx"/> is one the strip holds (always, for a full decode).</summary>
    private static bool InColumns(Component c, int bx) => (uint)(bx - c.BlkLo) <= (uint)(c.BlkHi - c.BlkLo);

    // ============================== marker parsing ==============================

    /// <summary>Handles DRI / DQT / DHT / APPn / COM. Returns false on malformed or unknown markers.</summary>
    private bool ProcessMarker(int m)
    {
        int l;
        switch (m)
        {
            case 0xff:
                return false; // expected marker

            case 0xDD: // DRI
                if (Get16() != 4)
                    return false;
                _restartInterval = Get16();
                return true;

            case 0xDB: // DQT
                l = Get16() - 2;
                while (l > 0)
                {
                    int q = Get8();
                    var p = q >> 4;
                    var sixteen = p != 0;
                    var t = q & 15;
                    if (p != 0 && p != 1)
                        return false;
                    if (t > 3)
                        return false;
                    for (var i = 0; i < 64; ++i)
                        _dequant[t][Dezigzag[i]] = (ushort)(sixteen ? Get16() : Get8());
                    l -= sixteen ? 129 : 65;
                }

                return l == 0;

            case 0xC4: // DHT
            {
                l = Get16() - 2;
                Span<int> sizes = stackalloc int[16];
                while (l > 0)
                {
                    int q = Get8();
                    var tc = q >> 4;
                    var th = q & 15;
                    if (tc > 1 || th > 3)
                        return false;
                    var n = 0;
                    for (var i = 0; i < 16; ++i)
                    {
                        sizes[i] = Get8();
                        n += sizes[i];
                    }

                    if (n > 256)
                        return false;
                    l -= 17;

                    var huff = tc == 0 ? _huffDc[th] : _huffAc[th];
                    if (!huff.Build(sizes))
                        return false;
                    for (var i = 0; i < n; ++i)
                        huff.Values[i] = Get8();
                    if (tc != 0)
                        huff.BuildFastAc(_fastAc[th]);
                    l -= n;
                }

                return l == 0;
            }
        }

        if ((m >= 0xE0 && m <= 0xEF) || m == 0xFE) // APPn / COM
        {
            l = Get16();
            if (l < 2)
                return false;
            l -= 2;

            if (m == 0xE0 && l >= 5)
            {
                // APP0 JFIF identifier (upstream stb C semantics — see type remarks).
                ReadOnlySpan<byte> tag = "JFIF\0"u8;
                var ok = true;
                for (var i = 0; i < 5; ++i)
                {
                    if (Get8() != tag[i])
                        ok = false;
                }

                l -= 5;
                if (ok)
                    _jfif = true;
            }
            else if (m == 0xEE && l >= 12)
            {
                // APP14 Adobe: "Adobe" + version-hi (0), then version-lo, two
                // 16-bit flag words, and the colour-transform byte.
                ReadOnlySpan<byte> tag = "Adobe\0"u8;
                var ok = true;
                for (var i = 0; i < 6; ++i)
                {
                    if (Get8() != tag[i])
                        ok = false;
                }

                l -= 6;
                if (ok)
                {
                    Get8();  // version low byte
                    Get16(); // flags0
                    Get16(); // flags1
                    _app14ColorTransform = Get8();
                    l -= 6;
                }
            }

            Skip(l);
            return true;
        }

        return false; // unknown marker
    }

    private void ProcessScanHeader()
    {
        var ls = Get16();
        _scanN = Get8();
        if (_scanN < 1 || _scanN > 4 || _scanN > ImgN)
            throw Err("bad SOS component count");
        if (ls != 6 + 2 * _scanN)
            throw Err("bad SOS len");

        for (var i = 0; i < _scanN; ++i)
        {
            int id = Get8();
            int q = Get8();
            int which;
            for (which = 0; which < ImgN; ++which)
            {
                if (_comp[which].Id == id)
                    break;
            }

            if (which == ImgN)
                throw Err("bad SOS component id");
            _comp[which].Hd = q >> 4;
            if (_comp[which].Hd > 3)
                throw Err("bad DC huff");
            _comp[which].Ha = q & 15;
            if (_comp[which].Ha > 3)
                throw Err("bad AC huff");
            _order[i] = which;
        }

        _specStart = Get8();
        _specEnd = Get8();
        var aa = Get8();
        _succHigh = aa >> 4;
        _succLow = aa & 15;
        if (_progressive)
        {
            if (_specStart > 63 || _specEnd > 63 || _specStart > _specEnd || _succHigh > 13 || _succLow > 13)
                throw Err("bad SOS");
        }
        else
        {
            if (_specStart != 0)
                throw Err("bad SOS");
            if (_succHigh != 0 || _succLow != 0)
                throw Err("bad SOS");
            _specEnd = 63;
        }
    }

    private void ProcessFrameHeader(ScanMode scan)
    {
        var lf = Get16();
        if (lf < 11)
            throw Err("bad SOF len");
        var p = Get8();
        if (p != 8)
            throw Err("only 8-bit JPEG is supported");
        ImgY = Get16();
        if (ImgY == 0)
            throw Err("no header height");
        ImgX = Get16();
        if (ImgX == 0)
            throw Err("0 width");
        if (ImgY > 1 << 24 || ImgX > 1 << 24)
            throw Err("too large");
        var c = Get8();
        if (c != 3 && c != 1 && c != 4)
            throw Err("bad component count");
        ImgN = c;
        if (lf != 8 + 3 * ImgN)
            throw Err("bad SOF len");

        _rgbCount = 0;
        ReadOnlySpan<byte> rgbIds = "RGB"u8;
        for (var i = 0; i < ImgN; ++i)
        {
            var comp = _comp[i];
            comp.Id = Get8();
            if (ImgN == 3 && comp.Id == rgbIds[i])
                ++_rgbCount;
            var q = Get8();
            comp.H = q >> 4;
            if (comp.H == 0 || comp.H > 4)
                throw Err("bad H");
            comp.V = q & 15;
            if (comp.V == 0 || comp.V > 4)
                throw Err("bad V");
            comp.Tq = Get8();
            if (comp.Tq > 3)
                throw Err("bad TQ");
        }

        if (scan != ScanMode.Load)
            return;

        if ((long)ImgX * ImgY * ImgN > int.MaxValue)
            throw Err("too large");

        var hMax = 1;
        var vMax = 1;
        for (var i = 0; i < ImgN; ++i)
        {
            if (_comp[i].H > hMax)
                hMax = _comp[i].H;
            if (_comp[i].V > vMax)
                vMax = _comp[i].V;
        }

        for (var i = 0; i < ImgN; ++i)
        {
            if (hMax % _comp[i].H != 0)
                throw Err("bad H");
            if (vMax % _comp[i].V != 0)
                throw Err("bad V");
        }

        _imgHMax = hMax;
        _imgVMax = vMax;

        // MCU geometry is dictated by the bitstream and stays full-resolution;
        // only the IDCT output tile (and therefore the planes) shrink with scale.
        _imgMcuW = hMax * 8;
        _imgMcuH = vMax * 8;
        _imgMcuX = (ImgX + _imgMcuW - 1) / _imgMcuW;
        _imgMcuY = (ImgY + _imgMcuH - 1) / _imgMcuH;

        OutX = (ImgX + _scale - 1) / _scale;
        OutY = (ImgY + _scale - 1) / _scale;

        // A unit is what one pass of the entropy decoder fills before the rows it completes are assembled:
        // an MCU row of an interleaved scan, a block row when the only component is scanned on its own grid.
        _totalUnits = ImgN == 1 ? (ImgY + 7) >> 3 : _imgMcuY;

        // The whole image, until SetRegion narrows it.
        _unitStart = 0;
        _unitEnd = _totalUnits;
        _outEnd = OutY;
        _cropX = 0;
        _cropW = OutX;

        var b = _blockSize;
        for (var i = 0; i < ImgN; ++i)
        {
            var comp = _comp[i];
            comp.X = (ImgX * comp.H + hMax - 1) / hMax;
            comp.Y = (ImgY * comp.V + vMax - 1) / vMax;
            comp.W2 = _imgMcuX * comp.H * b;
            comp.H2 = _imgMcuY * comp.V * b;
            if ((long)comp.W2 * comp.H2 > int.MaxValue)
                throw Err("too large");

            comp.UnitBlockRows = ImgN == 1 ? 1 : comp.V;

            // No sample memory yet: strips (or, for the cases that cannot stream, whole planes) are
            // allocated when the first scan says which. See EnsureStrips / EnsurePlanes.

            if (_progressive)
            {
                comp.CoeffW = _imgMcuX * comp.H;
                comp.CoeffH = _imgMcuY * comp.V;
                var coeffLen = (long)comp.CoeffW * comp.CoeffH * 64;
                if (coeffLen > int.MaxValue)
                    throw Err("too large");
                comp.Coeff = ArrayPool<short>.Shared.Rent((int)coeffLen);
                comp.Coeff.AsSpan(0, (int)coeffLen).Clear();
            }
        }
    }

    private void DecodeHeader(ScanMode scan)
    {
        _jfif = false;
        _app14ColorTransform = -1;
        _marker = 0xff;
        int m = GetMarker();
        if (m != 0xd8)
            throw Err("no SOI");

        m = GetMarker();
        while (!(m == 0xc0 || m == 0xc1 || m == 0xc2))
        {
            if (!ProcessMarker(m))
                throw Err($"bad or unsupported marker 0x{m:X2} before SOF");
            m = GetMarker();
            while (m == 0xff)
            {
                if (AtEof)
                    throw Err("no SOF");
                m = GetMarker();
            }
        }

        _progressive = m == 0xc2;
        ProcessFrameHeader(scan);
    }

    /// <summary>
    /// Tolerates trailing garbage after entropy data: scans forward for the next
    /// plausible marker byte (stb's stbi__skip_jpeg_junk_at_end).
    /// </summary>
    private byte SkipJunkAtEnd()
    {
        while (!AtEof)
        {
            var x = Get8();
            while (x == 0xff)
            {
                if (AtEof)
                    return 0xff;
                x = Get8();
                if (x != 0x00 && x != 0xff)
                    return x;
            }
        }

        return 0xff;
    }

    /// <summary>
    /// The marker loop after the frame header. Returns false when the stream turned out not to stream (see
    /// <see cref="DecodeBody"/>); true otherwise, including when a bad marker made it stop early and salvage.
    /// </summary>
    private bool DecodeJpegImage<TSink>(ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        int m = GetMarker();
        while (m != 0xd9) // EOI
        {
            if (m == 0xda) // SOS
            {
                ProcessScanHeader();
                if (_streamedScan)
                    return false; // a later scan overwrites the streamed one's blocks: needs whole planes

                if (!_progressive && !_usePlanes)
                {
                    if (ScanCoversEveryComponent())
                        _streaming = true;
                    else
                        _usePlanes = true; // baseline scans that each carry part of the frame
                }

                if (_usePlanes)
                    EnsurePlanes();

                if (_streaming)
                {
                    _streamedScan = true;
                    EnsureStrips();
                    StreamScan(ref sink);
                }
                else
                {
                    ParseEntropyCodedData();
                }

                if (_marker == 0xff)
                    _marker = SkipJunkAtEnd();

                m = GetMarker();
                if (m >= 0xd0 && m <= 0xd7) // stray RSTn between scans
                    m = GetMarker();
            }
            else if (m == 0xdc) // DNL
            {
                var ld = Get16();
                var nl = Get16();
                if (ld != 4)
                    throw Err("bad DNL len");
                if (nl != ImgY)
                    throw Err("bad DNL height");
                m = GetMarker();
            }
            else
            {
                // stb salvages the scans decoded so far when a marker goes bad
                // after the header phase; mirror that (note: progressive data
                // that never reaches Finish() stays a zeroed plane).
                if (!ProcessMarker(m))
                    return true;
                m = GetMarker();
            }
        }

        if (_progressive)
            Finish(ref sink);

        return true;
    }

    /// <summary>Progressive only: dequantize the accumulated coefficients and IDCT every block.</summary>
    private void Finish<TSink>(ref TSink sink)
        where TSink : IJpegRowSink, allows ref struct
    {
        if (_usePlanes)
        {
            EnsurePlanes();
            FinishWholePlanes();
        }
        else
        {
            FinishStreamed(ref sink);
        }
    }

    // ============================== helpers ==============================

    private static InvalidDataException Err(string message) => new("JPEG: " + message);

    private static bool AddIntsValid(int a, int b)
    {
        if (a >= 0 != b >= 0)
            return true;
        if (a < 0 && b < 0)
            return a >= int.MinValue - b;
        return a <= int.MaxValue - b;
    }

    private static bool Mul2ShortsValid(int a, int b)
    {
        if (b == 0 || b == -1)
            return true;
        if (a >= 0 == b >= 0)
            return a <= short.MaxValue / b;
        if (b < 0)
            return a <= short.MinValue / b;
        return a >= short.MinValue / b;
    }
}
