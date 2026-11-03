namespace SharpAstro.Radiance;

/// <summary>What the three stored channels of a Radiance picture mean (the <c>FORMAT=</c> header).</summary>
public enum RadianceFormat
{
    /// <summary><c>32-bit_rle_rgbe</c>: scene-linear red, green, blue.</summary>
    Rgbe,

    /// <summary><c>32-bit_rle_xyze</c>: CIE X, Y, Z tristimulus. The reader reports it and does not convert it.</summary>
    Xyze,
}

/// <summary>
/// A decoded Radiance picture: <see cref="Width"/> x <see cref="Height"/> pixels of three scene-linear
/// floats, top scanline first (the only orientation this codec reads, <c>-Y h +X w</c>).
/// </summary>
public sealed class RadianceImage
{
    /// <summary>Image width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Interleaved samples, <c>Width * Height * 3</c> floats. Channels are R,G,B for
    /// <see cref="RadianceFormat.Rgbe"/> and X,Y,Z for <see cref="RadianceFormat.Xyze"/>.</summary>
    public required float[] Pixels { get; init; }

    /// <summary>What the channels mean.</summary>
    public RadianceFormat Format { get; init; } = RadianceFormat.Rgbe;

    /// <summary>
    /// The product of every <c>EXPOSURE=</c> header line, or null when there is none. It is reported and
    /// <b>not applied</b>: <see cref="Pixels"/> are the stored values. Per the Radiance format, a pixel's
    /// real-world value is its stored value divided by this exposure.
    /// </summary>
    public double? Exposure { get; init; }

    /// <summary>The header lines between the magic line and the blank line, verbatim (comments, FORMAT=, EXPOSURE=, ...).</summary>
    public IReadOnlyList<string> HeaderLines { get; init; } = [];
}
