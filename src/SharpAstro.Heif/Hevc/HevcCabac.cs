using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpAstro.Heif.Hevc;

/// <summary>
/// Where each syntax element's contexts sit in <see cref="HevcCabac"/>'s state array, and their
/// initialisation values for initType 0 (I slices), from H.265 Tables 9-5 to 9-37. Only the elements an
/// intra slice of the decoded envelope codes are here.
/// </summary>
internal static class HevcContext
{
    public const int SplitCuFlag = 0;                        // 3
    public const int PartMode = SplitCuFlag + 3;             // 1 (intra: bin 0 only)
    public const int PrevIntraLumaPredFlag = PartMode + 1;   // 1
    public const int IntraChromaPredMode = PrevIntraLumaPredFlag + 1; // 1
    public const int SplitTransformFlag = IntraChromaPredMode + 1;    // 3
    public const int CbfLuma = SplitTransformFlag + 3;       // 2
    public const int CbfChroma = CbfLuma + 2;                // 5, shared by cbf_cb and cbf_cr
    public const int CuQpDeltaAbs = CbfChroma + 5;           // 2
    public const int LastXPrefix = CuQpDeltaAbs + 2;         // 18
    public const int LastYPrefix = LastXPrefix + 18;         // 18
    public const int CodedSubBlockFlag = LastYPrefix + 18;   // 4
    public const int SigCoeffFlag = CodedSubBlockFlag + 4;   // 42
    public const int Greater1Flag = SigCoeffFlag + 42;       // 24
    public const int Greater2Flag = Greater1Flag + 24;       // 6
    public const int SaoMergeFlag = Greater2Flag + 6;        // 1, shared by sao_merge_left_flag and sao_merge_up_flag
    public const int SaoTypeIdx = SaoMergeFlag + 1;          // 1 (bin 0; bin 1 is bypass), luma and chroma
    public const int TransformSkipFlag = SaoTypeIdx + 1;     // 2: luma, chroma
    public const int CuTransquantBypassFlag = TransformSkipFlag + 2; // 1
    public const int Count = CuTransquantBypassFlag + 1;

    /// <summary>initValue per context, in the layout above.</summary>
    public static ReadOnlySpan<byte> IntraInitValues =>
    [
        139, 141, 157,                                                         // split_cu_flag
        184,                                                                   // part_mode
        184,                                                                   // prev_intra_luma_pred_flag
        63,                                                                    // intra_chroma_pred_mode
        153, 138, 138,                                                         // split_transform_flag
        111, 141,                                                              // cbf_luma
        94, 138, 182, 154, 154,                                                // cbf_cb, cbf_cr
        154, 154,                                                              // cu_qp_delta_abs
        110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111, 79, 108, 123, 63, // last_sig_coeff_x_prefix
        110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111, 79, 108, 123, 63, // last_sig_coeff_y_prefix
        91, 171, 134, 141,                                                     // coded_sub_block_flag
        111, 111, 125, 110, 110, 94, 124, 108, 124, 107, 125, 141, 179, 153, 125, 107, 125, 141, // sig_coeff_flag (luma 0..17)
        179, 153, 125, 107, 125, 141, 179, 153, 125,                           //   luma 18..26
        140, 139, 182, 182, 152, 136, 152, 136, 153, 136, 139, 111, 136, 139, 111, //   chroma 27..41
        140, 92, 137, 138, 140, 152, 138, 139, 153, 74, 149, 92,               // coeff_abs_level_greater1_flag (luma)
        139, 107, 122, 152, 140, 179, 166, 182, 140, 227, 122, 197,            //   (chroma from 16)
        138, 153, 136, 167, 152, 152,                                          // coeff_abs_level_greater2_flag
        153,                                                                   // sao_merge_left_flag, sao_merge_up_flag
        200,                                                                   // sao_type_idx_luma, sao_type_idx_chroma
        139, 139,                                                              // transform_skip_flag (luma, chroma)
        154,                                                                   // cu_transquant_bypass_flag
    ];
}

/// <summary>
/// The CABAC arithmetic decoding engine (H.265 9.3.4.3): context-coded decisions, bypass bins and the
/// terminating bin, over a slice's data in RBSP form.
/// <para>
/// Running out of data is not an end of input to CABAC: like the MQ coder of JBIG2 and JPEG 2000, it would
/// keep producing bins from whatever it is fed. So bits past the end read as zero, which every bounded loop
/// in the syntax tolerates, and more than <see cref="OverreadLimitBits"/> of them is refused as a slice that
/// was cut short. Every unbounded prefix in the binarisations is capped by its caller.
/// </para>
/// </summary>
internal sealed class HevcCabac
{
    /// <summary>Table 9-52 (rangeTabLps), indexed <c>[pStateIdx * 4 + qRangeIdx]</c>.</summary>
    private static ReadOnlySpan<byte> RangeTabLps =>
    [
        128, 176, 208, 240, 128, 167, 197, 227, 128, 158, 187, 216, 123, 150, 178, 205,
        116, 142, 169, 195, 111, 135, 160, 185, 105, 128, 152, 175, 100, 122, 144, 166,
        95, 116, 137, 158, 90, 110, 130, 150, 85, 104, 123, 142, 81, 99, 117, 135,
        77, 94, 111, 128, 73, 89, 105, 122, 69, 85, 100, 116, 66, 80, 95, 110,
        62, 76, 90, 104, 59, 72, 86, 99, 56, 69, 81, 94, 53, 65, 77, 89,
        51, 62, 73, 85, 48, 59, 69, 80, 46, 56, 66, 76, 43, 53, 63, 72,
        41, 50, 59, 69, 39, 48, 56, 65, 37, 45, 54, 62, 35, 43, 51, 59,
        33, 41, 48, 56, 32, 39, 46, 53, 30, 37, 43, 50, 29, 35, 41, 48,
        27, 33, 39, 45, 26, 31, 37, 43, 24, 30, 35, 41, 23, 28, 33, 39,
        22, 27, 32, 37, 21, 26, 30, 35, 20, 24, 29, 33, 19, 23, 27, 31,
        18, 22, 26, 30, 17, 21, 25, 28, 16, 20, 23, 27, 15, 19, 22, 25,
        14, 18, 21, 24, 14, 17, 20, 23, 13, 16, 19, 22, 12, 15, 18, 21,
        12, 14, 17, 20, 11, 14, 16, 19, 11, 13, 15, 18, 10, 12, 15, 17,
        10, 12, 14, 16, 9, 11, 13, 15, 9, 11, 12, 14, 8, 10, 12, 14,
        8, 9, 11, 13, 7, 9, 11, 12, 7, 9, 10, 12, 7, 8, 10, 11,
        6, 8, 9, 11, 6, 7, 9, 10, 6, 7, 8, 9, 2, 2, 2, 2,
    ];

    /// <summary>Table 9-53, transIdxLps: the state after a least probable symbol.</summary>
    private static ReadOnlySpan<byte> TransIdxLps =>
    [
        0, 0, 1, 2, 2, 4, 4, 5, 6, 7, 8, 9, 9, 11, 11, 12, 13, 13, 15, 15, 16, 16, 18, 18, 19, 19, 21, 21, 22, 22, 23, 24,
        24, 25, 26, 26, 27, 27, 28, 29, 29, 30, 30, 30, 31, 32, 32, 33, 33, 33, 34, 34, 35, 35, 35, 36, 36, 36, 37, 37, 37, 38, 38, 63,
    ];

    public const int OverreadLimitBits = 64;

    private readonly byte[] _data;
    private readonly int _end;
    private readonly int _start;    // where the slice data begins, for counting the bits consumed
    private int _position;      // next byte to load
    private ulong _cache;       // bits loaded but not consumed, the next one in bit 63
    private int _cached;        // how many of _cache's bits are loaded
    private int _zeroBytes;     // bytes loaded past the end of the data, which read as zero

    private uint _range;
    private uint _offset;

    // Per context: pStateIdx << 1 | valMps.
    private readonly byte[] _state = new byte[HevcContext.Count];

    public HevcCabac(byte[] data, int start, int end)
    {
        _data = data;
        _position = start;
        _start = start;
        _end = end;
    }

    /// <summary>9.3.2.2: every context from its initValue at SliceQpY, then 9.3.2.5: the engine.</summary>
    public void Start(int sliceQpY)
    {
        InitializeContexts(sliceQpY);
        StartEngine();
    }

    /// <summary>9.3.2.2: every context from its initValue at SliceQpY.</summary>
    public void InitializeContexts(int sliceQpY)
    {
        var init = HevcContext.IntraInitValues;
        var qp = Math.Clamp(sliceQpY, 0, 51);
        for (var i = 0; i < _state.Length; i++)
        {
            var slope = (init[i] >> 4) * 5 - 45;
            var offset = ((init[i] & 15) << 3) - 16;
            var pre = Math.Clamp(((slope * qp) >> 4) + offset, 1, 126);
            _state[i] = pre <= 63 ? (byte)((63 - pre) << 1) : (byte)(((pre - 64) << 1) | 1);
        }
    }

    /// <summary>The context variables, for the wavefront synchronisation (9.3.2.4) to restore.</summary>
    public void SaveContexts(byte[] into) => _state.AsSpan().CopyTo(into);

    public void RestoreContexts(byte[] from) => from.AsSpan(0, _state.Length).CopyTo(_state);

    /// <summary>
    /// After pcm_flag (a terminating bin of 1): pcm_alignment_zero_bits from exactly where the arithmetic
    /// decoder stopped up to the byte boundary, after which <see cref="ReadPcmSample"/> reads the raw samples
    /// and <see cref="RestartAfterPcm"/> starts the engine again (9.3.2.5) wherever they end. The start was
    /// settled on the conformance streams: of the positions one bit either side and this one, only this one
    /// decodes all six PCM streams as ffmpeg does, and the alignment bits are checked to be zero.
    /// </summary>
    public void BeginPcm()
    {
        var padding = _cached % 8;
        if (ReadBits(padding) != 0) throw new InvalidDataException("pcm_alignment_zero_bits are not zero.");
    }

    public int ReadPcmSample(int bits) => (int)ReadBits(bits);

    public void RestartAfterPcm() => StartEngine();

    /// <summary>
    /// Restarts the engine (9.3.2.5) at RBSP byte <paramref name="start"/>, where an entry point puts the next
    /// substream, after the terminating bin that ended this one. It must lie past what this substream read.
    /// </summary>
    public void StartSubstreamAt(int start)
    {
        var consumed = (long)(_position - _start + _zeroBytes) * 8 - _cached;
        // _zeroBytes would count the cache's look-ahead past the end of the data, not bytes consumed: a real
        // over-read is refused by ReadBits, so it is not tested here (a substream ending in the slice's last few
        // bytes, as a tall flat picture's rows do, is valid).
        if (start < _start + consumed / 8 || start >= _end)
            throw new InvalidDataException($"An entry point puts a substream at byte {start}, but the one before it reads to bit {consumed} of the slice data.");
        _position = start;
        _cache = 0;
        _cached = 0;
        _zeroBytes = 0; // the previous substream's look-ahead past the end, not this one's
        StartEngine();
    }

    private void StartEngine()
    {
        _range = 510;
        _offset = ReadBits(9);
        // 9.3.2.5: a conforming stream never starts with 510 or 511.
        if (_offset >= 510) throw new InvalidDataException("The CABAC stream starts with an offset of 510 or 511.");
    }

    /// <summary>
    /// The next <paramref name="count"/> bits (0 to 32), first bit most significant. Up to eight bytes are
    /// loaded ahead of what is consumed; past the end of the data they read as zero, and consuming more than
    /// <see cref="OverreadLimitBits"/> of those is a slice that ended before its last coding tree unit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadBits(int count)
    {
        if (_cached < count) Refill();
        var value = (uint)(_cache >> 32 >> (32 - count)); // two shifts, so a count of 0 gives 0 rather than a shift by 64
        _cache <<= count;
        _cached -= count;
        if (_zeroBytes != 0 && _zeroBytes * 8 - _cached > OverreadLimitBits)
            throw new InvalidDataException("The slice data ended before its last coding tree unit.");
        return value;
    }

    private void Refill()
    {
        while (_cached <= 56)
        {
            ulong next = 0;
            if (_position < _end) next = _data[_position++];
            else _zeroBytes++;
            _cache |= next << (56 - _cached);
            _cached += 8;
        }
    }

    /// <summary>9.3.4.3.2: one context-coded bin.</summary>
    public int Decision(int context)
    {
        var state = _state[context];
        var pState = state >> 1;
        var mps = state & 1;
        var lps = RangeTabLps[pState * 4 + (int)((_range >> 6) & 3)];
        _range -= lps;

        int bin;
        if (_offset >= _range)
        {
            bin = 1 - mps;
            _offset -= _range;
            _range = lps;
            if (pState == 0) mps = 1 - mps;
            _state[context] = (byte)(TransIdxLps[pState] << 1 | mps);
        }
        else
        {
            bin = mps;
            _state[context] = (byte)(Math.Min(pState + 1, 62) << 1 | mps);
        }

        // Renormalise (9.3.4.3.3): double the range until it is at least 256, one bit into the offset per doubling.
        if (_range < 256)
        {
            var shift = BitOperations.LeadingZeroCount(_range) - 23;
            _range <<= shift;
            _offset = _offset << shift | ReadBits(shift);
        }
        return bin;
    }

    /// <summary>9.3.4.3.4: one bypass bin.</summary>
    public int Bypass()
    {
        _offset = _offset << 1 | ReadBits(1);
        if (_offset >= _range)
        {
            _offset -= _range;
            return 1;
        }
        return 0;
    }

    /// <summary><paramref name="count"/> bypass bins as an unsigned number, first bin most significant.</summary>
    /// <remarks>
    /// One bypass bin is a step of binary long division: shift a bit into the offset, and the bin is 1 when
    /// the offset reaches the range, which is then subtracted. The offset is always below the range, so
    /// <paramref name="count"/> steps are the division of the offset with <paramref name="count"/> bits shifted
    /// in by the range: the quotient is the bins, the remainder the new offset. That is what this computes, in
    /// one division rather than <paramref name="count"/> steps; <c>HevcCabacTests</c> holds it to the bin-by-bin form.
    /// </remarks>
    public int Bypass(int count)
    {
        if (count <= 0) return 0;
        if (count > 32) throw new InvalidDataException($"{count} bypass bins in one value.");
        var dividend = (ulong)_offset << count | ReadBits(count);
        var quotient = dividend / _range;
        _offset = (uint)(dividend - quotient * _range);
        return (int)quotient;
    }

    /// <summary>The same bins one at a time, as 9.3.4.3.4 writes them: the reference for <see cref="Bypass(int)"/>.</summary>
    internal int BypassBinByBin(int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++) value = value << 1 | Bypass();
        return value;
    }

    /// <summary>9.3.4.3.5: the terminating bin (end_of_slice_segment_flag).</summary>
    public int Terminate()
    {
        _range -= 2;
        if (_offset >= _range) return 1;
        if (_range < 256)
        {
            var shift = BitOperations.LeadingZeroCount(_range) - 23;
            _range <<= shift;
            _offset = _offset << shift | ReadBits(shift);
        }
        return 0;
    }
}
