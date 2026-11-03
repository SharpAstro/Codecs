namespace SharpAstro.Jpeg;

/// <summary>
/// A rectangle of output pixels for <see cref="JpegDecoder.DecodeRegionTo"/>, in the coordinates of the
/// <b>decode scale</b>: at <see cref="JpegScale.Half"/> it indexes the half-size image
/// (<see cref="JpegInfo.ScaledSize"/>), not the source frame. <paramref name="X"/> and <paramref name="Y"/> are the
/// top-left pixel, counted from the top-left of the image; the rectangle must be non-empty and lie entirely inside
/// the scaled image.
/// </summary>
/// <param name="X">Left column, 0-based.</param>
/// <param name="Y">Top row, 0-based.</param>
/// <param name="Width">Width in pixels; at least 1.</param>
/// <param name="Height">Height in pixels; at least 1.</param>
public readonly record struct JpegRegion(int X, int Y, int Width, int Height);
