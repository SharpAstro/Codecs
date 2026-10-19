using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;

namespace SharpAstro.Webp;

internal ref struct Vp8BoolDecoder
{
    // Cursor + remaining count instead of a Span: the refill loop runs once per
    // decoded byte and bounds checks were showing up in the profile.
    private ref byte _next;
    private int _remaining;
    private int _bitsAvailable;
    private ulong _bitBuffer;
    private ulong _value;
    private int _range;

    public Vp8BoolDecoder(ReadOnlySpan<byte> data)
    {
        _next = ref Unsafe.AsRef(in MemoryMarshal.GetReference(data));
        _remaining = data.Length;
        _bitsAvailable = 0;
        _bitBuffer = 0;
        _range = 255;
        _value = (data.Length > 0 ? (ulong)data[0] : 0) << 8 |
                 (data.Length > 1 ? (ulong)data[1] : 0);
        int skip = Math.Min(data.Length, 2);
        _next = ref Unsafe.Add(ref _next, skip);
        _remaining -= skip;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit(byte probability)
    {
        int split = 1 + ((_range - 1) * probability >> 8);
        int boundary = split << 8;
        int bit;
        if (_value >= (ulong)boundary)
        {
            _value -= (uint)boundary;
            _range -= split;
            bit = 1;
        }
        else
        {
            _range = split;
            bit = 0;
        }

        int shift = BitOperations.LeadingZeroCount((uint)_range) - 24;
        if (shift != 0)
        {
            _range <<= shift;
            _value = ((_value << shift) | (uint)ReadInputBits(shift)) & 0xffffUL;
        }

        return bit;
    }

    public int ReadLiteral(int bitCount)
    {
        int value = 0;
        for (int i = 0; i < bitCount; i++)
        {
            value = (value << 1) | ReadBit(128);
        }

        return value;
    }

    public int ReadSignedLiteral(int bitCount)
    {
        int value = ReadLiteral(bitCount);
        return ReadBit(128) != 0 ? -value : value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadInputBits(int count)
    {
        while (_bitsAvailable < count && _bitsAvailable <= 56 && _remaining > 0)
        {
            _bitBuffer |= (ulong)_next << (56 - _bitsAvailable);
            _next = ref Unsafe.Add(ref _next, 1);
            _remaining--;
            _bitsAvailable += 8;
        }

        int take = Math.Min(count, _bitsAvailable);
        int value = (int)(_bitBuffer >> (64 - take));
        _bitBuffer <<= take;
        _bitsAvailable -= take;
        return take == count ? value : value << (count - take);
    }
}

internal ref struct Vp8TokenPartitions
{
    private Vp8BoolDecoder _p0;
    private Vp8BoolDecoder _p1;
    private Vp8BoolDecoder _p2;
    private Vp8BoolDecoder _p3;
    private Vp8BoolDecoder _p4;
    private Vp8BoolDecoder _p5;
    private Vp8BoolDecoder _p6;
    private Vp8BoolDecoder _p7;

    public Vp8TokenPartitions(ReadOnlySpan<byte> p0, ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2,
        ReadOnlySpan<byte> p3, ReadOnlySpan<byte> p4, ReadOnlySpan<byte> p5,
        ReadOnlySpan<byte> p6, ReadOnlySpan<byte> p7)
    {
        _p0 = new Vp8BoolDecoder(p0);
        _p1 = new Vp8BoolDecoder(p1);
        _p2 = new Vp8BoolDecoder(p2);
        _p3 = new Vp8BoolDecoder(p3);
        _p4 = new Vp8BoolDecoder(p4);
        _p5 = new Vp8BoolDecoder(p5);
        _p6 = new Vp8BoolDecoder(p6);
        _p7 = new Vp8BoolDecoder(p7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Vp8BoolDecoder PartitionRef(int partition)
    {
        // Unsafe.AsRef bypasses CS8170 (structs may not return instance fields by
        // ref); safe here because callers can only use the ref while this ref
        // struct is alive.
        switch (partition)
        {
            case 0: return ref Unsafe.AsRef(in _p0);
            case 1: return ref Unsafe.AsRef(in _p1);
            case 2: return ref Unsafe.AsRef(in _p2);
            case 3: return ref Unsafe.AsRef(in _p3);
            case 4: return ref Unsafe.AsRef(in _p4);
            case 5: return ref Unsafe.AsRef(in _p5);
            case 6: return ref Unsafe.AsRef(in _p6);
            case 7: return ref Unsafe.AsRef(in _p7);
            default: throw new ArgumentOutOfRangeException(nameof(partition));
        }
    }
}
