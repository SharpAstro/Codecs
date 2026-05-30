namespace SharpAstro.Exr;

/// <summary>
/// OpenEXR's canonical-Huffman codec (a faithful port of OpenEXRCore's internal_huf.c),
/// used by PIZ. Symbols are 16-bit values plus one pseudo run-length-code symbol; the
/// compressed stream is a 20-byte header (im, iM, tableLength, nBits, 0) followed by the
/// packed code-length table and the MSB-first bit stream. Because OpenEXR rebuilds the
/// canonical codes from the transmitted code <i>lengths</i> (<see cref="CanonicalCodeTable"/>),
/// our output decodes in OpenEXR and theirs decodes here.
/// </summary>
internal static class ExrHuffman
{
    private const int EncBits = 16, DecBits = 14;
    private const int EncSize = (1 << EncBits) + 1;   // 65537
    private const int DecSize = 1 << DecBits;          // 16384
    private const int DecMask = DecSize - 1;
    private const int ShortZeroRun = 59, LongZeroRun = 63;
    private const int ShortestLongRun = 2 + LongZeroRun - ShortZeroRun; // 6
    private const int LongestLongRun = 255 + ShortestLongRun;           // 261

    private static int HufLength(ulong code) => (int)(code & 63);
    private static ulong HufCode(ulong code) => code >> 6;

    // ----------------------------------------------------------------- compress

    public static byte[] Compress(ReadOnlySpan<ushort> data)
    {
        int n = data.Length;
        if (n == 0) return new byte[20];

        var freq = new ulong[EncSize];
        for (var i = 0; i < n; i++) freq[data[i]]++;

        BuildEncTable(freq, out int im, out int iM);

        var table = new BitSink();
        PackEncTable(freq, im, iM, table);
        byte[] tableBytes = table.ToArray();

        var enc = new BitSink();
        long nBits = Encode(freq, data, iM, enc);
        byte[] dataBytes = enc.ToArray();

        var outp = new byte[20 + tableBytes.Length + dataBytes.Length];
        WriteU32(outp, 0, (uint)im);
        WriteU32(outp, 4, (uint)iM);
        WriteU32(outp, 8, (uint)tableBytes.Length);
        WriteU32(outp, 12, (uint)nBits);
        WriteU32(outp, 16, 0);
        tableBytes.CopyTo(outp, 20);
        dataBytes.CopyTo(outp, 20 + tableBytes.Length);
        return outp;
    }

    // ----------------------------------------------------------------- decompress

    public static ushort[] Decompress(ReadOnlySpan<byte> compressed, int nRaw)
    {
        var raw = new ushort[nRaw];
        if (nRaw == 0) return raw;
        if (compressed.Length < 20) throw new InvalidDataException("EXR Huffman block too small.");

        int im = (int)ReadU32(compressed, 0);
        int iM = (int)ReadU32(compressed, 4);
        int nBits = (int)ReadU32(compressed, 12);
        if (im >= EncSize || iM >= EncSize) throw new InvalidDataException("Corrupt EXR Huffman header.");

        var hcode = new ulong[EncSize];
        int p = 20;
        UnpackEncTable(compressed, ref p, im, iM, hcode);

        var dec = new HufDec[DecSize];
        for (var i = 0; i < DecSize; i++) dec[i] = new HufDec();
        BuildDecTable(hcode, im, iM, dec);

        Decode(hcode, dec, compressed, p, nBits, iM, raw);
        return raw;
    }

    // ----------------------------------------------------------------- canonical codes

    private static void CanonicalCodeTable(ulong[] hcode)
    {
        var nc = new ulong[59];
        for (var i = 0; i < EncSize; i++) nc[hcode[i]]++;

        ulong c = 0;
        for (var i = 58; i > 0; --i)
        {
            ulong next = (c + nc[i]) >> 1;
            nc[i] = c;
            c = next;
        }

        for (var i = 0; i < EncSize; i++)
        {
            ulong l = hcode[i];
            if (l > 0) hcode[i] = l | (nc[l]++ << 6);
        }
    }

    // ----------------------------------------------------------------- encoding table (heap)

    private static void BuildEncTable(ulong[] freq, out int im, out int iM)
    {
        var hlink = new int[EncSize];
        var fHeap = new int[EncSize];
        var scode = new ulong[EncSize];
        int nf = 0;

        int lo = 0;
        while (freq[lo] == 0) lo++;
        im = lo;

        int hi = lo;
        for (var i = lo; i < EncSize; i++)
        {
            hlink[i] = i;
            if (freq[i] != 0) { fHeap[nf++] = i; hi = i; }
        }

        // Pseudo-symbol (run-length code) with frequency 1.
        hi++;
        freq[hi] = 1;
        fHeap[nf++] = hi;
        iM = hi;

        MakeHeap(fHeap, nf, freq);

        while (nf > 1)
        {
            int mm = fHeap[0];
            PopHeap(fHeap, nf, freq); nf--;
            int m = fHeap[0];
            PopHeap(fHeap, nf, freq);

            freq[m] += freq[mm];
            PushHeap(fHeap, nf, freq);

            for (int j = m; ; j = hlink[j]) { scode[j]++; if (hlink[j] == j) { hlink[j] = mm; break; } }
            for (int j = mm; ; j = hlink[j]) { scode[j]++; if (hlink[j] == j) break; }
        }

        CanonicalCodeTable(scode);
        Array.Copy(scode, freq, EncSize);
    }

    // FHeapCompare: order by frequency, tie-break by symbol index (deterministic).
    private static bool FLess(int a, int b, ulong[] freq) => freq[a] > freq[b] || (freq[a] == freq[b] && a > b);

    private static void InternPushHeap(int[] h, int holeIndex, int topIndex, int value, ulong[] freq)
    {
        int parent = (holeIndex - 1) / 2;
        while (holeIndex > topIndex && FLess(h[parent], value, freq))
        {
            h[holeIndex] = h[parent];
            holeIndex = parent;
            parent = (holeIndex - 1) / 2;
        }
        h[holeIndex] = value;
    }

    private static void AdjustHeap(int[] h, int holeIndex, int len, int value, ulong[] freq)
    {
        int topIndex = holeIndex;
        int secondChild = holeIndex;
        while (secondChild < (len - 1) / 2)
        {
            secondChild = 2 * (secondChild + 1);
            if (FLess(h[secondChild], h[secondChild - 1], freq)) --secondChild;
            h[holeIndex] = h[secondChild];
            holeIndex = secondChild;
        }
        if ((len & 1) == 0 && secondChild == (len - 2) / 2)
        {
            secondChild = 2 * (secondChild + 1);
            h[holeIndex] = h[secondChild - 1];
            holeIndex = secondChild - 1;
        }
        InternPushHeap(h, holeIndex, topIndex, value, freq);
    }

    private static void PushHeap(int[] h, int count, ulong[] freq)
        => InternPushHeap(h, count - 1, 0, h[count - 1], freq);

    private static void PopHeap(int[] h, int count, ulong[] freq)
    {
        if (count > 1)
        {
            int last = count - 1;
            int value = h[last];
            h[last] = h[0];
            AdjustHeap(h, 0, last, value, freq);
        }
    }

    private static void MakeHeap(int[] h, int len, ulong[] freq)
    {
        if (len < 2) return;
        int parent = (len - 2) / 2;
        while (true)
        {
            AdjustHeap(h, parent, len, h[parent], freq);
            if (parent == 0) return;
            --parent;
        }
    }

    // ----------------------------------------------------------------- pack / unpack table

    private static void PackEncTable(ulong[] hcode, int im, int iM, BitSink o)
    {
        for (; im <= iM; im++)
        {
            int l = HufLength(hcode[im]);
            if (l == 0)
            {
                int zerun = 1;
                while (im < iM && zerun < LongestLongRun)
                {
                    if (HufLength(hcode[im + 1]) > 0) break;
                    im++; zerun++;
                }
                if (zerun >= 2)
                {
                    if (zerun >= ShortestLongRun)
                    {
                        o.Put(6, LongZeroRun);
                        o.Put(8, (ulong)(zerun - ShortestLongRun));
                    }
                    else
                    {
                        o.Put(6, (ulong)(ShortZeroRun + zerun - 2));
                    }
                    continue;
                }
            }
            o.Put(6, (ulong)l);
        }
        o.Flush();
    }

    private static void UnpackEncTable(ReadOnlySpan<byte> src, ref int pos, int im, int iM, ulong[] hcode)
    {
        var br = new BitReader(src, pos);
        for (; im <= iM; im++)
        {
            ulong l = hcode[im] = br.Get(6);
            if (l == LongZeroRun)
            {
                int zerun = (int)br.Get(8) + ShortestLongRun;
                if (im + zerun > iM + 1) throw new InvalidDataException("Corrupt EXR Huffman table (long run).");
                while (zerun-- > 0) hcode[im++] = 0;
                im--;
            }
            else if (l >= ShortZeroRun)
            {
                int zerun = (int)l - ShortZeroRun + 2;
                if (im + zerun > iM + 1) throw new InvalidDataException("Corrupt EXR Huffman table (short run).");
                while (zerun-- > 0) hcode[im++] = 0;
                im--;
            }
        }
        pos = br.BytePos;
        CanonicalCodeTable(hcode);
    }

    // ----------------------------------------------------------------- decoding table

    private sealed class HufDec
    {
        public int Len;
        public int Lit;
        public List<int>? P;
    }

    private static void BuildDecTable(ulong[] hcode, int im, int iM, HufDec[] dec)
    {
        for (; im <= iM; im++)
        {
            ulong c = HufCode(hcode[im]);
            int l = HufLength(hcode[im]);
            if ((c >> l) != 0) throw new InvalidDataException("Corrupt EXR Huffman code.");

            if (l > DecBits)
            {
                var pl = dec[(int)(c >> (l - DecBits))];
                if (pl.Len != 0) throw new InvalidDataException("Corrupt EXR Huffman dec table.");
                pl.Lit++;
                (pl.P ??= []).Add(im);
            }
            else if (l != 0)
            {
                int start = (int)(c << (DecBits - l));
                for (long i = 1L << (DecBits - l); i > 0; i--, start++)
                {
                    var pl = dec[start];
                    if (pl.Len != 0 || pl.P != null) throw new InvalidDataException("Corrupt EXR Huffman dec table.");
                    pl.Len = l;
                    pl.Lit = im;
                }
            }
        }
    }

    // ----------------------------------------------------------------- encode / decode

    private static long Encode(ulong[] hcode, ReadOnlySpan<ushort> data, int rlc, BitSink o)
    {
        int n = data.Length;
        ulong runCode = hcode[rlc];
        int s = data[0];
        int cs = 0;
        for (var i = 1; i < n; i++)
        {
            int ns = data[i];
            if (cs == 255 || s != ns) { SendCode(hcode[s], cs, runCode, o); cs = 0; s = ns; }
            else cs++;
        }
        SendCode(hcode[s], cs, runCode, o);
        return o.TotalBits;
    }

    private static void SendCode(ulong sCode, int runCount, ulong runCode, BitSink o)
    {
        if (HufLength(sCode) + HufLength(runCode) + 8 < HufLength(sCode) * runCount)
        {
            o.Put(HufLength(sCode), HufCode(sCode));
            o.Put(HufLength(runCode), HufCode(runCode));
            o.Put(8, (ulong)runCount);
        }
        else
        {
            while (runCount-- >= 0) o.Put(HufLength(sCode), HufCode(sCode));
        }
    }

    private static void Decode(ulong[] hcode, HufDec[] dec, ReadOnlySpan<byte> src, int start, int nBits, int rlc, ushort[] outp)
    {
        int no = outp.Length;
        int oi = 0;          // output index
        ulong c = 0; int lc = 0;
        int inPos = start;
        int ie = start + (nBits + 7) / 8;

        while (inPos < ie)
        {
            c = (c << 8) | src[inPos++]; lc += 8;
            while (lc >= DecBits)
            {
                int decoffset = (int)((c >> (lc - DecBits)) & DecMask);
                var pl = dec[decoffset];
                if (pl.Len != 0)
                {
                    if (pl.Len > lc) throw new InvalidDataException("Corrupt EXR Huffman stream.");
                    lc -= pl.Len;
                    GetCode(pl.Lit, rlc, ref c, ref lc, src, ref inPos, ie, outp, ref oi, no);
                }
                else
                {
                    if (pl.P == null) throw new InvalidDataException("Corrupt EXR Huffman code (no long list).");
                    int j;
                    for (j = 0; j < pl.Lit; j++)
                    {
                        int sym = pl.P[j];
                        int l = HufLength(hcode[sym]);
                        while (lc < l && inPos < ie) { c = (c << 8) | src[inPos++]; lc += 8; }
                        if (lc >= l && HufCode(hcode[sym]) == ((c >> (lc - l)) & ((1UL << l) - 1)))
                        {
                            lc -= l;
                            GetCode(sym, rlc, ref c, ref lc, src, ref inPos, ie, outp, ref oi, no);
                            break;
                        }
                    }
                    if (j == pl.Lit) throw new InvalidDataException("Corrupt EXR Huffman long code.");
                }
            }
        }

        int shift = (8 - nBits) & 7;
        c >>= shift; lc -= shift;
        while (lc > 0)
        {
            int decoffset = (int)((c << (DecBits - lc)) & DecMask);
            var pl = dec[decoffset];
            if (pl.Len == 0) throw new InvalidDataException("Corrupt EXR Huffman trailing code.");
            if (pl.Len > lc) throw new InvalidDataException("Corrupt EXR Huffman trailing code length.");
            lc -= pl.Len;
            GetCode(pl.Lit, rlc, ref c, ref lc, src, ref inPos, ie, outp, ref oi, no);
        }

        if (oi != no) throw new InvalidDataException($"EXR Huffman produced {oi} of {no} expected symbols.");
    }

    private static void GetCode(int po, int rlc, ref ulong c, ref int lc, ReadOnlySpan<byte> src, ref int inPos, int ie, ushort[] outp, ref int oi, int no)
    {
        if (po == rlc)
        {
            if (lc < 8)
            {
                if (inPos >= ie) throw new InvalidDataException("Corrupt EXR Huffman run (no length byte).");
                c = (c << 8) | src[inPos++]; lc += 8;
            }
            lc -= 8;
            int cs = (int)((byte)(c >> lc));
            if (oi + cs > no) throw new InvalidDataException("Corrupt EXR Huffman run (overflow).");
            if (oi - 1 < 0) throw new InvalidDataException("Corrupt EXR Huffman run (underflow).");
            ushort sv = outp[oi - 1];
            while (cs-- > 0) outp[oi++] = sv;
        }
        else if (oi < no) { outp[oi++] = (ushort)po; }
        else throw new InvalidDataException("Corrupt EXR Huffman output overflow.");
    }

    // ----------------------------------------------------------------- bit / byte helpers

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    // MSB-first bit accumulator (mirrors OpenEXR's outputBits).
    private sealed class BitSink
    {
        private readonly List<byte> _bytes = [];
        private ulong _c;
        private int _lc;

        public void Put(int nBits, ulong bits)
        {
            if (nBits < 64) bits &= (1UL << nBits) - 1;
            _c = (_c << nBits) | bits;
            _lc += nBits;
            while (_lc >= 8) { _lc -= 8; _bytes.Add((byte)(_c >> _lc)); }
        }

        public long TotalBits => (long)_bytes.Count * 8 + _lc;

        // Emit the final partial byte (left-justified), as PackEncTable / Encode do at the end.
        public void Flush()
        {
            if (_lc > 0) { _bytes.Add((byte)(_c << (8 - _lc))); _lc = 0; }
        }

        public byte[] ToArray()
        {
            // Encode's bit count includes the trailing partial byte; emit it without resetting semantics.
            if (_lc > 0)
            {
                var copy = new byte[_bytes.Count + 1];
                _bytes.CopyTo(copy);
                copy[^1] = (byte)(_c << (8 - _lc));
                return copy;
            }
            return [.. _bytes];
        }
    }

    // MSB-first bit reader (mirrors OpenEXR's getBits).
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _src;
        private int _pos;
        private ulong _c;
        private int _lc;

        public BitReader(ReadOnlySpan<byte> src, int pos) { _src = src; _pos = pos; _c = 0; _lc = 0; }

        public int BytePos => _pos;

        public ulong Get(int nBits)
        {
            while (_lc < nBits) { _c = (_c << 8) | _src[_pos++]; _lc += 8; }
            _lc -= nBits;
            return (_c >> _lc) & ((1UL << nBits) - 1);
        }
    }
}
