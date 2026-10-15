namespace SharpAstro.Webp;

internal ref struct Vp8BoolDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private int _nextBit;
    private uint _value;
    private int _range;

    public Vp8BoolDecoder(ReadOnlySpan<byte> data)
    {
        _data = data;
        _nextBit = 16;
        _range = 255;
        _value = (uint)ReadInputBitAt(0) << 15 |
                 (uint)ReadInputBitAt(1) << 14 |
                 (uint)ReadInputBitAt(2) << 13 |
                 (uint)ReadInputBitAt(3) << 12 |
                 (uint)ReadInputBitAt(4) << 11 |
                 (uint)ReadInputBitAt(5) << 10 |
                 (uint)ReadInputBitAt(6) << 9 |
                 (uint)ReadInputBitAt(7) << 8 |
                 (uint)ReadInputBitAt(8) << 7 |
                 (uint)ReadInputBitAt(9) << 6 |
                 (uint)ReadInputBitAt(10) << 5 |
                 (uint)ReadInputBitAt(11) << 4 |
                 (uint)ReadInputBitAt(12) << 3 |
                 (uint)ReadInputBitAt(13) << 2 |
                 (uint)ReadInputBitAt(14) << 1 |
                 (uint)ReadInputBitAt(15);
    }

    public int ReadBit(byte probability)
    {
        int split = 1 + ((_range - 1) * probability >> 8);
        int boundary = split << 8;
        int bit;
        if (_value >= boundary)
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

        while (_range < 128)
        {
            _range <<= 1;
            _value = ((_value << 1) | (uint)ReadInputBitAt(_nextBit++)) & 0xffff;
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

    private int ReadInputBitAt(int bitIndex)
    {
        if (bitIndex < 0 || (bitIndex >> 3) >= _data.Length)
        {
            return 0;
        }

        return (_data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1;
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
