using System.Buffers;
using System.Numerics;

namespace SharpAstro.Jpeg;

/// <summary>
/// The decoder engine: marker parsing, Huffman/entropy decode (baseline +
/// progressive), restart intervals, per-component sample planes, and the final
/// upsample + colour-convert pass. The full-scale pipeline is a 1:1 port of
/// stb_image's JPEG decoder (the in-repo <c>StbImage.Generated.Jpg.cs</c> is the
/// reference source) so output is byte-exact against StbImageSharp; departures:
///
/// <list type="bullet">
/// <item>Component planes, progressive coefficient buffers, and line buffers are
/// rented from <see cref="ArrayPool{T}"/> instead of malloc'd — and zeroed, so
/// plane padding is deterministic where stb reads malloc garbage (valid streams
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

    internal int OutX { get; private set; }
    internal int OutY { get; private set; }

    public JpegCore(ReadOnlySpan<byte> data, int scale)
    {
        _data = data;
        _scale = scale;
        _blockSize = 8 / scale;
    }

    private sealed class Component
    {
        public int Id, H, V, Tq, Hd, Ha;
        public int DcPred;
        public int X, Y;             // full-resolution sample dimensions
        public int W2, H2;           // scaled plane stride / height
        public byte[]? Data;         // pooled sample plane (W2 * H2)
        public short[]? Coeff;       // pooled coefficient store (progressive only)
        public int CoeffW, CoeffH;   // in 8×8 blocks (full resolution)
        public byte[]? LineBuf;      // pooled upsample row buffer

        // per-decode resample state (mirrors stb's stbi__resample)
        public int Hs, Vs, YStep, YPos, WLores, YsScaled;
        public int Line0, Line1;     // row offsets into Data
        public int ResampleKind;
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
        return new JpegInfo(ImgX, ImgY, ImgN, _progressive);
    }

    /// <summary>Runs the full entropy decode into the (pooled) component planes.</summary>
    public void LoadImage()
    {
        DecodeJpegImage();
        OutX = (ImgX + _scale - 1) / _scale;
        OutY = (ImgY + _scale - 1) / _scale;
        if ((long)OutX * OutY * 4 > int.MaxValue)
            throw Err("too large");
    }

    /// <summary>Upsamples + colour-converts the decoded planes into packed RGBA rows.</summary>
    public void AssembleRgba(Span<byte> dest)
    {
        var isRgb = ImgN == 3 && (_rgbCount == 3 || (_app14ColorTransform == 0 && !_jfif));
        var decodeN = ImgN;

        for (var k = 0; k < decodeN; ++k)
        {
            var c = _comp[k];
            c.LineBuf = ArrayPool<byte>.Shared.Rent(OutX + 3);
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
        }

        // Current output-resolution row per component: (array, offset) because the
        // 1:1 kernel aliases the plane directly instead of copying.
        var rows = new (byte[] Arr, int Off)[4];

        for (var j = 0; j < OutY; ++j)
        {
            for (var k = 0; k < decodeN; ++k)
            {
                var c = _comp[k];
                var yBot = c.YStep >= (c.Vs >> 1);
                var inNear = yBot ? c.Line1 : c.Line0;
                var inFar = yBot ? c.Line0 : c.Line1;
                if (c.ResampleKind == JpegResample.OneToOne)
                {
                    rows[k] = (c.Data!, inNear);
                }
                else
                {
                    JpegResample.Row(c.ResampleKind, c.LineBuf, c.Data, inNear, inFar, c.WLores, c.Hs);
                    rows[k] = (c.LineBuf!, 0);
                }

                if (++c.YStep >= c.Vs)
                {
                    c.YStep = 0;
                    c.Line0 = c.Line1;
                    if (++c.YPos < c.YsScaled)
                        c.Line1 += c.W2;
                }
            }

            var outRow = dest.Slice(j * OutX * 4, OutX * 4);
            var y = rows[0].Arr.AsSpan(rows[0].Off);

            if (ImgN == 3)
            {
                if (isRgb)
                {
                    var g = rows[1].Arr.AsSpan(rows[1].Off);
                    var b = rows[2].Arr.AsSpan(rows[2].Off);
                    for (var i = 0; i < OutX; ++i)
                    {
                        outRow[i * 4 + 0] = y[i];
                        outRow[i * 4 + 1] = g[i];
                        outRow[i * 4 + 2] = b[i];
                        outRow[i * 4 + 3] = 255;
                    }
                }
                else
                {
                    JpegColor.YCbCrToRgbaRow(outRow, y, rows[1].Arr.AsSpan(rows[1].Off), rows[2].Arr.AsSpan(rows[2].Off), OutX);
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
                    for (var i = 0; i < OutX; ++i)
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
                    JpegColor.YCbCrToRgbaRow(outRow, y, c1, c2, OutX);
                    for (var i = 0; i < OutX; ++i)
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
                    JpegColor.YCbCrToRgbaRow(outRow, y, c1, c2, OutX);
                }
            }
            else // grayscale
            {
                for (var i = 0; i < OutX; ++i)
                {
                    var v = y[i];
                    outRow[i * 4 + 0] = v;
                    outRow[i * 4 + 1] = v;
                    outRow[i * 4 + 2] = v;
                    outRow[i * 4 + 3] = 255;
                }
            }
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

    /// <summary>Progressive only: dequantize the accumulated coefficients and IDCT every block.</summary>
    private void Finish()
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

    private void IdctToPlane(Component c, int bx, int by, scoped ReadOnlySpan<short> data)
    {
        var off = c.W2 * by * _blockSize + bx * _blockSize;
        if (_blockSize == 8)
            JpegIdct.Idct8x8(c.Data!, off, c.W2, data);
        else
            JpegIdct.IdctReduced(c.Data!, off, c.W2, data, _blockSize);
    }

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

            var planeLen = comp.W2 * comp.H2;
            comp.Data = ArrayPool<byte>.Shared.Rent(planeLen);
            // stb leaves its malloc'd plane uninitialised; valid streams write
            // every block before any read, but zeroing keeps pooled reuse
            // deterministic for malformed inputs.
            comp.Data.AsSpan(0, planeLen).Clear();

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

    private void DecodeJpegImage()
    {
        _restartInterval = 0;
        DecodeHeader(ScanMode.Load);
        int m = GetMarker();
        while (m != 0xd9) // EOI
        {
            if (m == 0xda) // SOS
            {
                ProcessScanHeader();
                ParseEntropyCodedData();
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
                    return;
                m = GetMarker();
            }
        }

        if (_progressive)
            Finish();
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
