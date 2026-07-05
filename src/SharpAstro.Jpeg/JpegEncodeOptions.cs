namespace SharpAstro.Jpeg;

/// <summary>Chroma subsampling for the encoder.</summary>
public enum JpegSubsampling : byte
{
    /// <summary>Pick from quality the way stb_image_write does: 4:2:0 when
    /// <see cref="JpegEncodeOptions.Quality"/> ≤ 90, else 4:4:4. This is the only
    /// mode whose bytes are validated against the reference encoder.</summary>
    Auto = 0,

    /// <summary>Full chroma resolution (no subsampling), one 8×8 block per component per MCU.</summary>
    Chroma444,

    /// <summary>2×2 chroma subsampling (4:2:0), four Y blocks + one each of Cb/Cr per 16×16 MCU.</summary>
    Chroma420,
}

/// <summary>
/// Options for <see cref="JpegEncoder.Encode"/>. Baseline sequential JPEG with
/// fixed Annex K Huffman tables (matching the stb_image_write reference the
/// encoder is ported from); progressive, restart intervals, and optimized
/// Huffman tables are deliberately out of scope for this baseline.
/// </summary>
public sealed record JpegEncodeOptions
{
    /// <summary>Quality 1..100 (libjpeg-style). Higher is larger and closer to the
    /// source. The default, 90, is the reference encoder's default and the highest
    /// quality that still uses 4:2:0 under <see cref="JpegSubsampling.Auto"/>.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>Chroma subsampling. <see cref="JpegSubsampling.Auto"/> derives it
    /// from <see cref="Quality"/> exactly as the reference encoder does.</summary>
    public JpegSubsampling Subsampling { get; init; } = JpegSubsampling.Auto;
}
