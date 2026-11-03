namespace SharpAstro.Pnm;

/// <summary>What the samples of a Netpbm image mean, as a PAM <c>TUPLTYPE</c> names it.</summary>
public enum PnmTupleType
{
    /// <summary>One sample, 0 = black, 1 = white (PBM, after the reader has undone PBM's inverted bits; PAM <c>BLACKANDWHITE</c>).</summary>
    BlackAndWhite,

    /// <summary>Black-and-white plus an alpha sample (PAM <c>BLACKANDWHITE_ALPHA</c>).</summary>
    BlackAndWhiteAlpha,

    /// <summary>One grey sample (PGM, PAM <c>GRAYSCALE</c>).</summary>
    Grayscale,

    /// <summary>Grey plus alpha (PAM <c>GRAYSCALE_ALPHA</c>).</summary>
    GrayscaleAlpha,

    /// <summary>Red, green, blue (PPM, PAM <c>RGB</c>).</summary>
    Rgb,

    /// <summary>Red, green, blue, alpha (PAM <c>RGB_ALPHA</c>).</summary>
    RgbAlpha,
}

/// <summary>
/// The header of a Netpbm file: everything needed to size the raster, read without touching a
/// pixel.
/// </summary>
/// <param name="Version">The magic number: 1..7 for <c>P1</c>..<c>P7</c>.</param>
/// <param name="Width">Pixels across (&gt; 0).</param>
/// <param name="Height">Pixels down (&gt; 0).</param>
/// <param name="MaxValue">The native maximum sample value, 1..65535; 1 for PBM. Above 255 raw samples are two bytes, big-endian.</param>
/// <param name="TupleType">What the samples mean; for P1-P6 implied by the magic, for P7 by <c>TUPLTYPE</c> (or <c>DEPTH</c> when absent).</param>
/// <param name="DataOffset">Offset of the raster's first byte in the file.</param>
public readonly record struct PnmHeader(
    int Version,
    int Width,
    int Height,
    int MaxValue,
    PnmTupleType TupleType,
    int DataOffset)
{
    /// <summary>True for the plain (ASCII) forms P1, P2 and P3.</summary>
    public bool IsPlain => Version <= 3;

    /// <summary>True for P7 (PAM).</summary>
    public bool IsPam => Version == 7;

    /// <summary>True for P1 / P4 (PBM): bits, 1 = black in the file.</summary>
    public bool IsBitmap => Version is 1 or 4;

    /// <summary>Samples per pixel: 1 (grey), 2 (grey + alpha), 3 (RGB) or 4 (RGBA).</summary>
    public int Channels => TupleType switch
    {
        PnmTupleType.BlackAndWhite or PnmTupleType.Grayscale => 1,
        PnmTupleType.BlackAndWhiteAlpha or PnmTupleType.GrayscaleAlpha => 2,
        PnmTupleType.Rgb => 3,
        _ => 4,
    };

    /// <summary>True when the last channel is alpha.</summary>
    public bool HasAlpha => Channels is 2 or 4;
}
