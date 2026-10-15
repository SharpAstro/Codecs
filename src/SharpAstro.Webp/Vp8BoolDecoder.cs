using System.Runtime.CompilerServices;
using System.Numerics;

namespace SharpAstro.Webp;

internal ref struct Vp8BoolDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private int _nextByte;
    private int _bitsAvailable;
    private ulong _bitBuffer;
    private ulong _value;
    private int _range;

    public Vp8BoolDecoder(ReadOnlySpan<byte> data)
    {
        _data = data;
        _nextByte = Math.Min(data.Length, 2);
        _bitsAvailable = 0;
        _bitBuffer = 0;
        _range = 255;
        _value = (data.Length > 0 ? (ulong)data[0] : 0) << 8 |
                 (data.Length > 1 ? (ulong)data[1] : 0);
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
        int value = 0;
        while (count != 0)
        {
            if (_bitsAvailable == 0)
            {
                int bytes = Math.Min(8, _data.Length - _nextByte);
                if (bytes == 0)
                {
                    return value << count;
                }

                ulong buffer = 0;
                for (int i = 0; i < bytes; i++)
                {
                    buffer |= (ulong)_data[_nextByte + i] << ((7 - i) * 8);
                }

                _nextByte += bytes;
                _bitBuffer = buffer;
                _bitsAvailable = bytes * 8;
            }

            int take = Math.Min(count, _bitsAvailable);
            value = (value << take) | (int)(_bitBuffer >> (64 - take));
            _bitBuffer <<= take;
            _bitsAvailable -= take;
            count -= take;
        }

        return value;
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

    public Vp8BoolDecoder GetPartition(int partition) => partition switch
    {
        0 => _p0,
        1 => _p1,
        2 => _p2,
        3 => _p3,
        4 => _p4,
        5 => _p5,
        6 => _p6,
        7 => _p7,
        _ => throw new ArgumentOutOfRangeException(nameof(partition)),
    };

    public void SetPartition(int partition, Vp8BoolDecoder decoder)
    {
        switch (partition)
        {
            case 0: _p0 = decoder; break;
            case 1: _p1 = decoder; break;
            case 2: _p2 = decoder; break;
            case 3: _p3 = decoder; break;
            case 4: _p4 = decoder; break;
            case 5: _p5 = decoder; break;
            case 6: _p6 = decoder; break;
            case 7: _p7 = decoder; break;
            default: throw new ArgumentOutOfRangeException(nameof(partition));
        }
    }

    public int ReadBit(int partition, byte probability) => partition switch
    {
        0 => _p0.ReadBit(probability),
        1 => _p1.ReadBit(probability),
        2 => _p2.ReadBit(probability),
        3 => _p3.ReadBit(probability),
        4 => _p4.ReadBit(probability),
        5 => _p5.ReadBit(probability),
        6 => _p6.ReadBit(probability),
        7 => _p7.ReadBit(probability),
        _ => throw new ArgumentOutOfRangeException(nameof(partition)),
    };
}
