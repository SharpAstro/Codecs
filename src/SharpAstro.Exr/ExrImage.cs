namespace SharpAstro.Exr;

/// <summary>
/// An in-memory OpenEXR scanline image: a set of channels, each holding raw
/// little-endian samples in top-to-bottom, left-to-right raster order
/// (<see cref="Width"/> × <see cref="Height"/> samples of the channel's pixel type).
/// This is the codec-level representation; <see cref="ExrFile"/> serializes it and
/// the higher-level facade converts to/from <c>float</c> / <see cref="System.Half"/>.
/// </summary>
public sealed class ExrImage
{
    /// <summary>Image width in pixels (data-window width).</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels (data-window height).</summary>
    public required int Height { get; init; }

    /// <summary>Compression applied to the pixel blocks.</summary>
    public ExrCompression Compression { get; set; } = ExrCompression.Zip;

    /// <summary>Scanline storage order. This codec writes <see cref="ExrLineOrder.IncreasingY"/>.</summary>
    public ExrLineOrder LineOrder { get; set; } = ExrLineOrder.IncreasingY;

    private readonly List<ExrChannel> _channels = [];
    private readonly List<byte[]> _data = [];

    /// <summary>The channels, in insertion order (serialization re-sorts to name order).</summary>
    public IReadOnlyList<ExrChannel> Channels => _channels;

    /// <summary>Raw little-endian samples for the channel at <paramref name="index"/>
    /// (<c>Width*Height*BytesPerSample</c>, row-major, top scanline first).</summary>
    public byte[] GetData(int index) => _data[index];

    /// <summary>Raw samples for the named channel, or throws if absent.</summary>
    public byte[] GetData(string name)
    {
        int i = IndexOf(name);
        if (i < 0) throw new KeyNotFoundException($"EXR image has no channel '{name}'.");
        return _data[i];
    }

    /// <summary>True if a channel with the given name is present.</summary>
    public bool HasChannel(string name) => IndexOf(name) >= 0;

    public int IndexOf(string name)
    {
        for (var i = 0; i < _channels.Count; i++)
            if (_channels[i].Name == name) return i;
        return -1;
    }

    /// <summary>Add a channel and its raw little-endian sample bytes.</summary>
    public void AddChannel(ExrChannel channel, byte[] pixels)
    {
        int expected = Width * Height * channel.BytesPerSample;
        if (pixels.Length != expected)
            throw new ArgumentException($"Channel '{channel.Name}' expects {expected} bytes ({Width}x{Height}x{channel.BytesPerSample}), got {pixels.Length}.", nameof(pixels));
        _channels.Add(channel);
        _data.Add(pixels);
    }
}
