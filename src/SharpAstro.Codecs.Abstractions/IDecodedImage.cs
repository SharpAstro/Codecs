using System;

namespace SharpAstro.Codecs.Abstractions;

/// <summary>
/// A codec-neutral decoded raster - the fidelity tier of the codecs facade. It
/// preserves the source bit depth and channel count so a downstream consumer
/// (e.g. an astro pipeline working in 16-bit / float) sees the real sample
/// values rather than a lossy 8-bit projection.
/// <para>
/// <see cref="Pixels"/> is interleaved, row-major, tightly packed (no row
/// padding), in host byte order. The convenience <see cref="ToRgba8"/>
/// down-converts to 8-bit RGBA for the display tier (e.g. a terminal / UI raster).
/// </para>
/// </summary>
public interface IDecodedImage
{
    /// <summary>Pixel width.</summary>
    int Width { get; }

    /// <summary>Pixel height.</summary>
    int Height { get; }

    /// <summary>Samples per pixel: 1 (gray), 2 (gray+alpha), 3 (RGB), 4 (RGBA).</summary>
    int Channels { get; }

    /// <summary>The per-channel sample type in <see cref="Pixels"/>.</summary>
    SampleFormat SampleFormat { get; }

    /// <summary>
    /// Interleaved, row-major, tightly-packed pixel samples in host byte order.
    /// The row stride is <c>Width * Channels * bytesPerSample</c> with no padding,
    /// where <c>bytesPerSample</c> follows from <see cref="SampleFormat"/>.
    /// </summary>
    ReadOnlySpan<byte> Pixels { get; }

    /// <summary>
    /// The embedded ICC colour profile, or an empty span when the source carried
    /// none. Informational for a colour-managed consumer; never rescales pixels.
    /// </summary>
    ReadOnlySpan<byte> IccProfile { get; }

    /// <summary>
    /// Down-converts to a freshly-allocated, tightly-packed 8-bit RGBA buffer
    /// (row-major, 4 bytes/pixel, R,G,B,A byte order). 16-bit samples are scaled
    /// by <c>&gt;&gt; 8</c>; gray expands across R/G/B; a missing alpha becomes 255.
    /// </summary>
    byte[] ToRgba8();
}
