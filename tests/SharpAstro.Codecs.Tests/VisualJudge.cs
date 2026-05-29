using System.Runtime.InteropServices;
using ImageMagick;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Magick.NET-backed visual-diff judge for the JXR codec. Compares a decoded
/// raster against a reference and reports a normalized RMSE <em>distortion</em>
/// in [0, 1] (0 = identical). On any nonzero difference it writes a
/// red-highlighted diff PNG into <see cref="ArtifactsDir"/> so a regression can
/// be eyeballed without re-running anything by hand.
/// </summary>
/// <remarks>
/// Modeled on drawboard/bullclip-backend's PDF export comparison harness
/// (<c>ExportComparisonTests</c> / <c>ComparisonHelper</c>), which does
/// <c>imgA.Compare(imgB, ErrorMetric.RootMeanSquared, out var distortion)</c>
/// and saves the highlighted diff as a build artifact.
///
/// <para>Lets us swap "open the PNG and squint" for a number + an artifact:
/// encode → decode (ours, or via the jxrlib / WIC oracle) → judge against the
/// source. The Q16-HDRI Magick build keeps 16-bit and float comparisons
/// faithful for the BD16 / BD16F / BD32F pipeline.</para>
/// </remarks>
public static class VisualJudge
{
    /// <summary>Where highlighted diff PNGs land: <c>&lt;test bin&gt;/VisualDiffs/</c>.</summary>
    public static string ArtifactsDir { get; } = Path.Combine(AppContext.BaseDirectory, "VisualDiffs");

    /// <summary>Outcome of a comparison. <see cref="Distortion"/> is normalized RMSE in [0, 1].</summary>
    public readonly record struct DiffResult(double Distortion, string? DiffImagePath, int Width, int Height)
    {
        public bool IsBelow(double threshold) => Distortion <= threshold;

        public override string ToString() =>
            $"distortion(RMSE)={Distortion:G6} on {Width}x{Height}" +
            (DiffImagePath is null ? "" : $"  diff→ {DiffImagePath}");
    }

    // ---------------------------------------------------------------- files

    /// <summary>Compare two raster files (PNG/TIFF/BMP/... — anything Magick reads).</summary>
    public static DiffResult CompareFiles(
        string expectedPath,
        string actualPath,
        string? label = null,
        ErrorMetric metric = ErrorMetric.RootMeanSquared,
        bool alwaysWriteDiff = false)
    {
        using var expected = new MagickImage(expectedPath);
        using var actual = new MagickImage(actualPath);
        return CompareCore(expected, actual, label ?? Path.GetFileNameWithoutExtension(actualPath), metric, alwaysWriteDiff);
    }

    // ------------------------------------------------------------ raw 8-bit

    /// <summary>Compare two packed 24-bit RGB buffers (3 bytes/pixel, row-major).</summary>
    public static DiffResult CompareRgb24(
        ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int width, int height,
        string label, bool alwaysWriteDiff = false)
        => CompareRaw(expected, actual, width, height, StorageType.Char, "RGB", label, alwaysWriteDiff);

    /// <summary>Compare two packed 32-bit RGBA buffers (4 bytes/pixel, row-major).</summary>
    public static DiffResult CompareRgba32(
        ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int width, int height,
        string label, bool alwaysWriteDiff = false)
        => CompareRaw(expected, actual, width, height, StorageType.Char, "RGBA", label, alwaysWriteDiff);

    /// <summary>Compare two 8-bit grayscale buffers (1 byte/pixel, row-major).</summary>
    public static DiffResult CompareGray8(
        ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int width, int height,
        string label, bool alwaysWriteDiff = false)
    {
        // Replicate the single channel to RGB so both the comparison and the
        // saved diff artifact read naturally (gray-on-gray with red highlights).
        var e = GrayToRgb(expected);
        var a = GrayToRgb(actual);
        return CompareRaw(e, a, width, height, StorageType.Char, "RGB", label, alwaysWriteDiff);
    }

    // ----------------------------------------------------------- raw 16-bit

    /// <summary>Compare two packed 48-bit RGB buffers (3 host-endian ushorts/pixel).</summary>
    public static DiffResult CompareRgb48(
        ReadOnlySpan<ushort> expected, ReadOnlySpan<ushort> actual, int width, int height,
        string label, bool alwaysWriteDiff = false)
        => CompareRaw(MemoryMarshal.AsBytes(expected), MemoryMarshal.AsBytes(actual),
                      width, height, StorageType.Short, "RGB", label, alwaysWriteDiff);

    /// <summary>Compare two 16-bit grayscale buffers (1 host-endian ushort/pixel).</summary>
    public static DiffResult CompareGray16(
        ReadOnlySpan<ushort> expected, ReadOnlySpan<ushort> actual, int width, int height,
        string label, bool alwaysWriteDiff = false)
        => CompareRaw(MemoryMarshal.AsBytes(expected), MemoryMarshal.AsBytes(actual),
                      width, height, StorageType.Short, "R", label, alwaysWriteDiff);

    // ------------------------------------------------------------ raw float

    /// <summary>Compare two packed RGB float buffers (3 floats/pixel).</summary>
    public static DiffResult CompareRgbF32(
        ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, int width, int height,
        string label, bool alwaysWriteDiff = false)
        => CompareRaw(MemoryMarshal.AsBytes(expected), MemoryMarshal.AsBytes(actual),
                      width, height, StorageType.Float, "RGB", label, alwaysWriteDiff);

    /// <summary>Compare two grayscale float buffers (1 float/pixel).</summary>
    public static DiffResult CompareGrayF32(
        ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, int width, int height,
        string label, bool alwaysWriteDiff = false)
        => CompareRaw(MemoryMarshal.AsBytes(expected), MemoryMarshal.AsBytes(actual),
                      width, height, StorageType.Float, "R", label, alwaysWriteDiff);

    // --------------------------------------------------------------- core

    /// <summary>
    /// Build two <see cref="MagickImage"/>s from raw pixel bytes and compare. The
    /// <paramref name="mapping"/> is an ImageMagick channel map ("RGB", "RGBA", "R", ...);
    /// <paramref name="storage"/> is the per-sample storage type.
    /// </summary>
    public static DiffResult CompareRaw(
        ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual,
        int width, int height, StorageType storage, string mapping,
        string label, bool alwaysWriteDiff = false,
        ErrorMetric metric = ErrorMetric.RootMeanSquared)
    {
        var settings = new PixelReadSettings((uint)width, (uint)height, storage, mapping);
        using var e = new MagickImage();
        using var a = new MagickImage();
        e.ReadPixels(expected, settings);
        a.ReadPixels(actual, settings);
        return CompareCore(e, a, label, metric, alwaysWriteDiff);
    }

    static DiffResult CompareCore(MagickImage expected, MagickImage actual, string label, ErrorMetric metric, bool alwaysWriteDiff)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            throw new ArgumentException(
                $"[{label}] size mismatch: expected {expected.Width}x{expected.Height}, actual {actual.Width}x{actual.Height}");
        }

        using var diff = expected.Compare(actual, metric, out double distortion);

        string? path = null;
        if (alwaysWriteDiff || distortion > 0)
        {
            Directory.CreateDirectory(ArtifactsDir);
            var safe = Sanitize(label);
            path = Path.Combine(ArtifactsDir, $"diff__{safe}.png");
            diff.Write(path, MagickFormat.Png);
        }

        return new DiffResult(distortion, path, (int)expected.Width, (int)expected.Height);
    }

    // --------------------------------------------------------------- helpers

    static byte[] GrayToRgb(ReadOnlySpan<byte> gray)
    {
        var rgb = new byte[gray.Length * 3];
        for (var i = 0; i < gray.Length; i++)
        {
            rgb[i * 3 + 0] = gray[i];
            rgb[i * 3 + 1] = gray[i];
            rgb[i * 3 + 2] = gray[i];
        }
        return rgb;
    }

    static string Sanitize(string label)
    {
        Span<char> buf = stackalloc char[label.Length];
        for (var i = 0; i < label.Length; i++)
        {
            var c = label[i];
            buf[i] = (char.IsLetterOrDigit(c) || c is '-' or '.') ? c : '_';
        }
        return new string(buf);
    }
}
