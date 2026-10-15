namespace SharpAstro.Webp;

/// <summary>The encoded representation used by a WebP image.</summary>
public enum WebpFormat
{
    Lossy,
    Lossless,
    Animated,
}

/// <summary>Dimensions and properties read from a WebP container.</summary>
public readonly record struct WebpInfo(
    int Width,
    int Height,
    bool HasAlpha,
    WebpFormat Format,
    int FrameCount);

/// <summary>A decoded WebP image in tightly packed, non-premultiplied RGBA8.</summary>
public sealed class WebpImage
{
    internal WebpImage(int width, int height, bool hasAlpha, byte[] rgba, byte[]? iccProfile)
    {
        Width = width;
        Height = height;
        HasAlpha = hasAlpha;
        Rgba = rgba;
        IccProfile = iccProfile ?? [];
    }

    public int Width { get; }
    public int Height { get; }
    public bool HasAlpha { get; }
    public byte[] Rgba { get; }
    public ReadOnlyMemory<byte> IccProfile { get; }
}
