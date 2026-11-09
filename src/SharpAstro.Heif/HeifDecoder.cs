using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif.Hevc;

namespace SharpAstro.Heif;

/// <summary>
/// A decoded HEIF image as full-range R'G'B' code values at its own bit depth: what the file's samples
/// are after the YCbCr conversion, before any transfer function is undone. <see cref="ToLinearRgbaFloat(out ColorEncoding)"/>
/// is the HDR projection for display.
/// </summary>
public sealed class HeifRgbImage
{
    internal HeifRgbImage(int width, int height, int bitDepth, ushort[] samples, ColorEncoding encoding, byte[]? icc)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        Samples = samples;
        ColorEncoding = encoding;
        IccProfile = icc;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bits per sample of <see cref="Samples"/> (8 to 12): the coded image's luma bit depth.</summary>
    public int BitDepth { get; }

    /// <summary>R, G, B per pixel, row-major, top-down, codes 0 .. 2^<see cref="BitDepth"/> - 1 (full range).</summary>
    public ushort[] Samples { get; }

    /// <summary>
    /// The primaries and transfer the samples are in (from the file's <c>nclx</c>, else the HEVC VUI);
    /// always <see cref="ColorEncoding.FullRange"/>, since a narrow-range image is expanded by the conversion.
    /// </summary>
    public ColorEncoding ColorEncoding { get; }

    /// <summary>The image's ICC profile when the file carries one (<c>colr</c> of type <c>prof</c> or <c>rICC</c>).</summary>
    public byte[]? IccProfile { get; }

    /// <summary>
    /// Linear-light RGBA floats (alpha 1), on the scale <c>PngImageDecoder.TryDecodeRgbaFloat</c> uses:
    /// for PQ and HLG <c>1.0 = 203 cd/m²</c> (BT.2408 reference white), for the SDR transfers
    /// <c>1.0 = SDR white</c>. The primaries are reported in <paramref name="encoding"/>, not converted.
    /// </summary>
    /// <exception cref="NotSupportedException">The transfer has no linearisation here (see <c>HdrTransfer.IsSupportedTransfer</c>).</exception>
    public float[] ToLinearRgbaFloat(out ColorEncoding encoding) => ToLinearRgbaFloat(out encoding, -1);

    /// <summary>
    /// The same, with the rows filled by up to <paramref name="maxDegreeOfParallelism"/> threads at once (-1 for
    /// as many as the machine runs). Each pixel is a function of its own samples alone, so the result does not
    /// depend on how the rows were shared out; the fill is a table look-up and store stream, so the time is
    /// mostly first touches of a fresh array of 16 bytes a pixel, which threads share well.
    /// </summary>
    internal float[] ToLinearRgbaFloat(out ColorEncoding encoding, int maxDegreeOfParallelism)
    {
        var transfer = ColorEncoding.Transfer;
        if (!HdrTransfer.IsSupportedTransfer(transfer))
            throw new NotSupportedException($"Transfer characteristics {(int)transfer} ({transfer}) has no float linearisation.");

        var pixels = Width * Height;
        var output = new float[(long)pixels * 4];
        var codes = 1 << BitDepth;
        var samples = Samples;
        // Bands of rows, about a quarter of a megapixel each, so a thumbnail stays on the calling thread.
        var bandPixels = 1 << 18;
        var bands = (int)Math.Max(1, (pixels + bandPixels - 1) / bandPixels);
        var parallelism = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
        if (transfer == TransferFunction.Hlg)
        {
            // The HLG OOTF couples a pixel's channels through its luminance.
            var scene = new float[codes];
            HdrTransfer.BuildHlgSceneLightTable(BitDepth, fullRange: true, scene);
            var weights = LumaWeights.For(ColorEncoding.Primaries);
            Parallel.For(0, bands, parallelism, band =>
            {
                var end = (int)Math.Min(pixels, (long)(band + 1) * bandPixels);
                for (var p = band * bandPixels; p < end; p++)
                {
                    HdrTransfer.HlgSceneToLinear(scene[samples[3 * p]], scene[samples[3 * p + 1]], scene[samples[3 * p + 2]], weights,
                        out output[4 * p], out output[4 * p + 1], out output[4 * p + 2]);
                    output[4 * p + 3] = 1f;
                }
            });
        }
        else
        {
            var table = new float[codes];
            HdrTransfer.BuildLinearTable(transfer, ColorEncoding.Primaries, BitDepth, fullRange: true, table);
            Parallel.For(0, bands, parallelism, band =>
            {
                var end = (int)Math.Min(pixels, (long)(band + 1) * bandPixels);
                for (var p = band * bandPixels; p < end; p++)
                {
                    output[4 * p] = table[samples[3 * p]];
                    output[4 * p + 1] = table[samples[3 * p + 1]];
                    output[4 * p + 2] = table[samples[3 * p + 2]];
                    output[4 * p + 3] = 1f;
                }
            });
        }

        encoding = new ColorEncoding
        {
            Primaries = ColorEncoding.Primaries,
            Transfer = TransferFunction.Linear,
            FullRange = true,
            Float = FloatSemantics.DisplayReferred,
        };
        return output;
    }
}

/// <summary>
/// Decodes a HEIF image (rung 3 of <c>roadmap/heif.md</c>): the HEVC pictures (rung 2), cropped to the
/// sizes the file declares, a grid's tiles composed, converted from YCbCr to R'G'B' and then cropped,
/// rotated and mirrored for display. Clean-room from ISO/IEC 23008-12 and ITU-T H.273.
/// <para>
/// <b>The YCbCr conversion is H.273's, and where libheif agrees it agrees exactly.</b> Chroma is upsampled by
/// repeating each sample (no filter), and each pixel follows H.273's equations in double precision, in code
/// units, rounded half up to the image's own bit depth. Measured against libheif (through Magick.NET): for
/// full-range 4:2:2, the Canon format, every sample matches (all 5.3 million of the 1632x1088 CR3 preview,
/// the whole 32-Mpixel R7 grid, and x265 pictures at 8, 10 and 12 bits under BT.601, BT.709 and BT.2020);
/// a bilinear upsampling differed by up to 479 codes there and truncation in half the samples. libheif
/// differs by a code or two for narrow range (on 25-60 % of samples) and for 4:2:0 at 8 bits (about 1 %),
/// where it evidently uses integer shortcuts of its own; this decoder keeps the spec's arithmetic there,
/// and the tests hold those cases to a measured tolerance, apart from the exact ones.
/// </para>
/// </summary>
public static class HeifDecoder
{
    /// <summary>
    /// Decodes <paramref name="item"/> (the primary item when null) of the HEIF file in <paramref name="file"/>.
    /// Pass one of <see cref="HeifFile.ThumbnailsOf"/> to decode a preview instead of the full image.
    /// </summary>
    /// <exception cref="InvalidDataException">The file or its HEVC data is malformed.</exception>
    /// <exception cref="NotSupportedException">The image uses something this decoder does not handle (named in the message).</exception>
    /// <param name="file">The whole HEIF file.</param>
    /// <param name="item">The image item to decode; the primary item when null.</param>
    /// <param name="maxDegreeOfParallelism">
    /// How many of a grid's tiles may decode at once: -1 (the default) for as many as the machine runs, 1 to
    /// decode on the calling thread alone. The result is the same either way.
    /// </param>
    public static HeifRgbImage Decode(ReadOnlySpan<byte> file, HeifItem? item = null, int maxDegreeOfParallelism = -1)
    {
        if (maxDegreeOfParallelism is 0 or < -1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), maxDegreeOfParallelism, "Use -1 for no limit, or a positive count.");
        var heif = HeifReader.Read(file);
        var image = item is null ? heif.DescribePrimary(file) : heif.Describe(file, item);
        if (image.Item.Type == "grid")
        {
            foreach (var tileId in image.Grid!.TileItemIds)
                RefuseUnknownEssentials(heif.GetItem(tileId)!);
        }
        RefuseUnknownEssentials(image.Item);

        var width = (int)image.Width;
        var height = (int)image.Height;
        var depth = image.Coding.BitDepthLuma;
        Colour? colour = null;
        ushort[] canvas;

        if (image.Grid is { } grid)
        {
            var tileWidth = (int)image.TileWidth;
            var tileHeight = (int)image.TileHeight;
            // Everything that needs the file's bytes, and every refusal a tile's parameter sets carry, before
            // any tile is decoded and before the canvas is allocated: a span cannot cross into the workers, an
            // unsupported tile 19 should not cost the decode of tiles 0 to 18, and a tile whose SPS does not fit
            // what the file declares should not cost the canvas.
            // The file is copied once and each tile gets its bytes as a slice of that copy; only a tile split
            // over several extents is concatenated, and those copies together may not exceed the file.
            var shared = file.ToArray();
            long concatenated = 0;
            var jobs = new List<(HevcDecoderConfiguration Coding, ReadOnlyMemory<byte> Data, int X0, int Y0)>();
            long largestPicture = 0;
            // The grid's whole coded canvas may be at most four times its output (plus a CTB): a legal grid's
            // last row and column may overhang the output by anything short of a tile, which on an axis of two
            // or more tiles stays under twice, while a few-pixel output over huge tiles is still refused.
            long codedArea = 0;
            for (var t = 0; t < grid.TileItemIds.Count; t++)
            {
                int x0 = t % grid.Columns * tileWidth, y0 = t / grid.Columns * tileHeight;
                if (x0 >= width || y0 >= height) continue; // a tile wholly outside the output size
                var tile = heif.GetItem(grid.TileItemIds[t])!;
                var coding = heif.Describe(file, tile).Coding;
                var (sps, _) = HevcDecoder.ParameterSets(coding);
                CheckCodedSize(sps, tileWidth, tileHeight, $"Tile {t}");
                codedArea += (long)sps.Width * sps.Height;
                if (codedArea > 4 * (long)width * height + 64 * 64)
                    throw new InvalidDataException($"The grid's HEVC pictures add up to more than four times its {width}x{height} output.");
                largestPicture = Math.Max(largestPicture, PictureBytes(sps));
                colour ??= Colour.For(image, sps.VideoSignal, sps.BitDepthLuma, sps.ChromaFormatIdc);
                var ranges = heif.ItemRanges(file.Length, tile);
                ReadOnlyMemory<byte> data;
                if (ranges.Count == 1)
                {
                    data = shared.AsMemory(ranges[0].Start, ranges[0].Length);
                }
                else
                {
                    var bytes = heif.ReadItemData(file, tile);
                    concatenated += bytes.Length;
                    if (concatenated > file.Length)
                        throw new InvalidDataException($"The grid's tiles split over several extents add up to more than the {file.Length}-byte file.");
                    data = bytes;
                }
                jobs.Add((coding, data, x0, y0));
            }
            canvas = new ushort[(long)width * height * 3];

            // As many tiles at once as the machine runs (or the caller allows), but no more decoded pictures
            // alive at once than LivePictureBytes holds: memory is the canvas plus that, not one picture per core.
            var degree = maxDegreeOfParallelism == -1 ? Environment.ProcessorCount : maxDegreeOfParallelism;
            degree = (int)Math.Clamp(LivePictureBytes / Math.Max(1, largestPicture), 1, degree);

            // The tiles are independent pictures and write disjoint rectangles of the canvas, so they are decoded
            // and converted in parallel; the result does not depend on the order. A failure stops the tiles after
            // it (Break runs every one before it), and the first failing tile's exception is the one thrown, so a
            // damaged file fails the same way however the work was scheduled.
            var failures = new Exception?[jobs.Count];
            var parallelism = new ParallelOptions { MaxDegreeOfParallelism = degree };
            Parallel.For(0, jobs.Count, parallelism, (j, state) =>
            {
                var job = jobs[j];
                try
                {
                    var picture = HevcDecoder.Decode(job.Coding, job.Data.Span);
                    Convert(picture, colour!, tileWidth, tileHeight, canvas, width, height, job.X0, job.Y0);
                }
                catch (Exception e)
                {
                    failures[j] = e;
                    state.Break();
                }
            });
            if (failures.FirstOrDefault(e => e is not null) is { } failure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
        else
        {
            var (sps, _) = HevcDecoder.ParameterSets(image.Coding);
            CheckCodedSize(sps, width, height, image.Item.ToString());
            if ((long)sps.Width * sps.Height > 2 * (long)width * height + 64 * 64)
                throw new InvalidDataException($"{image.Item}'s HEVC picture of {sps.Width}x{sps.Height} is more than twice its {width}x{height}.");
            canvas = new ushort[(long)width * height * 3];
            var picture = HevcDecoder.Decode(image.Coding, heif.ReadItemData(file, image.Item));
            colour = Colour.For(image, picture);
            Convert(picture, colour, width, height, canvas, width, height, 0, 0);
        }

        // Transformative properties, in the order the item lists them. 23008-12 requires clap, irot, imir in
        // that order, and for a conforming file this is that order; a file that lists them otherwise gets what
        // libheif does with it, which is to follow the file. They are applied to the R'G'B' result. libheif
        // applies them to the Y'CbCr planes instead, and measurably differs in three places: rotating a 4:2:2
        // image (its chroma becomes vertically subsampled, and libheif upsamples that with a filter, up to 117
        // codes away from replicating it as the unrotated image is); converting with the stream's VUI rather
        // than the file's nclx whenever it rotates; and rounding a clean aperture whose edge falls on a half
        // pixel the other way when a rotation follows it.
        foreach (var association in image.Item.Properties)
        {
            switch (association.Property)
            {
                case HeifCleanAperture clap:
                    (canvas, width, height) = Transforms.Crop(canvas, width, height, clap);
                    break;
                case HeifRotation { Degrees: not 0 } rotation:
                    (canvas, width, height) = Transforms.Rotate(canvas, width, height, rotation.Degrees);
                    break;
                case HeifMirror mirror:
                    Transforms.Mirror(canvas, width, height, mirror.Axis);
                    break;
            }
        }

        return new HeifRgbImage(width, height, depth, canvas, colour!.Encoding, image.Icc?.Profile);
    }

    /// <summary>
    /// The most decoded tile pictures (their planes and per-block maps, <see cref="PictureBytes"/>) a grid
    /// keeps alive at once: 1 GiB, which bounds how many tiles decode in parallel when the tiles are large.
    /// An R7 tile is about 7 MB, so this never limits a real grid below the core count.
    /// </summary>
    internal const long LivePictureBytes = 1L << 30;

    /// <summary>
    /// What decoding a picture of this SPS holds at its peak: two bytes a sample for luma and chroma, one byte
    /// per 4x4 block for each of the decoder's six per-block maps, and the copy of the luma plane SAO reads
    /// from while it filters (2 bytes a sample; the per-CTB arrays are noise beside these).
    /// </summary>
    internal static long PictureBytes(HevcSps sps)
    {
        long luma = (long)sps.Width * sps.Height;
        var chroma = sps.ChromaFormatIdc switch { 0 => 0, 1 => luma / 2, 2 => luma, _ => 2 * luma };
        return 2 * (luma + chroma) + 6 * (luma / 16) + (sps.SampleAdaptiveOffset ? 2 * luma : 0);
    }

    /// <summary>
    /// Checked from the SPS alone, before the canvas or any plane is allocated: the coded picture, less its
    /// conformance window, must hold the <paramref name="declaredWidth"/> x <paramref name="declaredHeight"/>
    /// the file declares for it. How much bigger it may be is checked by the caller: a single picture at most
    /// twice the area it shows plus one 64x64 CTB (Canon codes 320x320 for a 320x214 thumbnail, 1.5 times), a
    /// grid's tiles together at most four times the output plus one CTB (a legal last row or column overhangs
    /// by anything short of a tile). Beyond those the file is refused rather than decoded into memory it does
    /// not show; what is left is memory in proportion to what the file declares, up to <see cref="HeifLimits"/>.
    /// </summary>
    internal static void CheckCodedSize(HevcSps sps, int declaredWidth, int declaredHeight, string what)
    {
        var window = sps.ConformanceWindow;
        int shownWidth = sps.Width - window.Left - window.Right, shownHeight = sps.Height - window.Top - window.Bottom;
        if (declaredWidth > shownWidth || declaredHeight > shownHeight)
            throw new InvalidDataException($"{what} declares {declaredWidth}x{declaredHeight}, but its HEVC picture is {shownWidth}x{shownHeight}.");
    }

    /// <summary>An essential property this reader does not understand means the image cannot be shown right (23008-12 6.5.1).</summary>
    private static void RefuseUnknownEssentials(HeifItem item)
    {
        foreach (var association in item.Properties)
            if (association.Essential && association.Property is HeifUnknownProperty unknown)
                throw new NotSupportedException($"{item} has an essential '{unknown.BoxType}' property this decoder does not apply.");
    }

    /// <summary>
    /// One coded picture into the canvas at (<paramref name="x0"/>, <paramref name="y0"/>): the conformance
    /// window first, then the <paramref name="usedWidth"/> x <paramref name="usedHeight"/> the file declares
    /// for it (Canon codes 320x320 for a 320x214 thumbnail and says so only in <c>ispe</c>), clipped to the canvas.
    /// </summary>
    internal static void Convert(HevcPicture picture, Colour colour, int usedWidth, int usedHeight,
        ushort[] canvas, int canvasWidth, int canvasHeight, int x0, int y0)
    {
        var window = picture.ConformanceWindow;
        var codedWidth = picture.Width - window.Left - window.Right;
        var codedHeight = picture.Height - window.Top - window.Bottom;
        if (usedWidth > codedWidth || usedHeight > codedHeight)
            throw new InvalidDataException($"The image declares {usedWidth}x{usedHeight}, but its HEVC picture is {codedWidth}x{codedHeight}.");
        if (picture.BitDepthLuma != colour.BitDepth)
            throw new InvalidDataException("A grid's tiles are coded at different bit depths.");

        var (log2W, log2H) = picture.ChromaFormatIdc switch { 1 => (1, 1), 2 => (1, 0), _ => (0, 0) };
        var w = Math.Min(usedWidth, canvasWidth - x0);
        var h = Math.Min(usedHeight, canvasHeight - y0);
        var max = (1 << colour.BitDepth) - 1;
        if (picture.Cb is null)
        {
            // Monochrome (4:0:0): chroma at its mid-point, so every matrix gives R' = G' = B' = Y' expanded.
            var mid = (ushort)(1 << (colour.BitDepth - 1));
            for (var y = 0; y < h; y++)
            {
                var lumaRow = (y + window.Top) * picture.Width + window.Left;
                var output = ((long)(y0 + y) * canvasWidth + x0) * 3;
                for (var x = 0; x < w; x++, output += 3)
                    colour.ToRgb(picture.Luma[lumaRow + x], mid, mid, max, out canvas[output], out canvas[output + 1], out canvas[output + 2]);
            }
            return;
        }
        for (var y = 0; y < h; y++)
        {
            var ly = y + window.Top;
            var lumaRow = ly * picture.Width + window.Left;
            var chromaRow = (ly >> log2H) * picture.ChromaWidth;
            var output = ((long)(y0 + y) * canvasWidth + x0) * 3;
            for (var x = 0; x < w; x++)
            {
                var cx = (x + window.Left) >> log2W;
                colour.ToRgb(picture.Luma[lumaRow + x], picture.Cb![chromaRow + cx], picture.Cr![chromaRow + cx], max,
                    out canvas[output], out canvas[output + 1], out canvas[output + 2]);
                output += 3;
            }
        }
    }

    /// <summary>How the samples become R'G'B': the H.273 matrix and range, and what the result is in.</summary>
    internal sealed class Colour
    {
        public required int BitDepth { get; init; }
        public required ColorEncoding Encoding { get; init; }
        public required bool Identity { get; init; }
        public required bool FullRange { get; init; }
        public required double Kr { get; init; }
        public required double Kb { get; init; }

        /// <summary>
        /// From the <c>nclx</c> property; without one, from the HEVC VUI; without either, BT.709 primaries,
        /// the sRGB transfer and the BT.601 matrix at full range.
        /// </summary>
        public static Colour For(HeifImage image, HevcPicture picture) =>
            For(image, picture.VideoSignal, picture.BitDepthLuma, picture.ChromaFormatIdc);

        /// <summary>The same, from what a picture's SPS says, so a grid's colour is known before a tile is decoded.</summary>
        public static Colour For(HeifImage image, HevcVideoSignal? videoSignal, int bitDepth, int chromaFormatIdc)
        {
            ColorPrimaries primaries;
            TransferFunction transfer;
            MatrixCoefficients matrix;
            bool fullRange;
            if (image.Nclx is { } nclx)
            {
                (primaries, transfer, matrix, fullRange) = (nclx.Primaries, nclx.Transfer, nclx.Matrix, nclx.FullRange);
            }
            else if (videoSignal is { SignalTypePresent: true } vui)
            {
                // (A VUI with only a chroma location falls through to the default below, as no VUI does: H.265
                // would infer a narrow range from it, but then two files differing in that one flag would convert
                // with different ranges, and no colour was stated either way.)
                primaries = (ColorPrimaries)(vui.Primaries ?? (int)ColorPrimaries.BT709);
                transfer = (TransferFunction)(vui.Transfer ?? (int)TransferFunction.Srgb);
                matrix = (MatrixCoefficients)(vui.Matrix ?? (int)MatrixCoefficients.BT470BG);
                fullRange = vui.FullRange;
            }
            else
            {
                (primaries, transfer, matrix, fullRange) = (ColorPrimaries.BT709, TransferFunction.Srgb, MatrixCoefficients.BT470BG, true);
            }
            return Create(bitDepth, chromaFormatIdc, primaries, transfer, matrix, fullRange);
        }

        internal static Colour Create(int bitDepth, int chromaFormatIdc, ColorPrimaries primaries, TransferFunction transfer,
            MatrixCoefficients matrix, bool fullRange)
        {
            var (kr, kb, identity) = matrix switch
            {
                MatrixCoefficients.Identity => (0.0, 0.0, true),
                MatrixCoefficients.BT709 => (0.2126, 0.0722, false),
                MatrixCoefficients.Fcc => (0.30, 0.11, false),
                MatrixCoefficients.BT470BG or MatrixCoefficients.SMPTE170M or MatrixCoefficients.Unspecified => (0.299, 0.114, false),
                MatrixCoefficients.SMPTE240M => (0.212, 0.087, false),
                MatrixCoefficients.BT2020NCL => (0.2627, 0.0593, false),
                _ => throw new NotSupportedException($"Matrix coefficients {(int)matrix} ({matrix}) are not converted (only the non-constant-luminance matrices and identity are)."),
            };
            if (identity && chromaFormatIdc != 3)
                throw new InvalidDataException("The identity matrix (GBR) needs 4:4:4 samples.");

            return new Colour
            {
                BitDepth = bitDepth,
                Encoding = new ColorEncoding { Primaries = primaries, Transfer = transfer, FullRange = true },
                Identity = identity,
                FullRange = fullRange,
                Kr = kr,
                Kb = kb,
            };
        }

        /// <summary>
        /// H.273 8.3 in code units at the image's bit depth: Y and the zero-centred chroma (the narrow-range
        /// equations first expand them to full range), the matrix inverted, and each result rounded half up.
        /// Computing in code units rather than on [0, 1] signals matters: dividing by 2^n - 1 and multiplying
        /// back costs exact agreement with libheif on some samples, which code units keep (measured, see the class remarks).
        /// </summary>
        public void ToRgb(int y, int cb, int cr, int max, out ushort r, out ushort g, out ushort b)
        {
            if (Identity)
            {
                // GBR: Y is G, Cb is B, Cr is R, as coded.
                (r, g, b) = ((ushort)Math.Min(cr, max), (ushort)Math.Min(y, max), (ushort)Math.Min(cb, max));
                return;
            }

            double ey, ecb, ecr;
            if (FullRange)
            {
                var mid = 1 << (BitDepth - 1);
                (ey, ecb, ecr) = (y, cb - mid, cr - mid);
            }
            else
            {
                var shift = BitDepth - 8;
                ey = (y - (16 << shift)) * (double)max / (219 << shift);
                ecb = (cb - (128 << shift)) * (double)max / (224 << shift);
                ecr = (cr - (128 << shift)) * (double)max / (224 << shift);
            }

            var er = ey + 2 * (1 - Kr) * ecr;
            var eb = ey + 2 * (1 - Kb) * ecb;
            var eg = (ey - Kr * er - Kb * eb) / (1 - Kr - Kb);
            r = Code(er, max);
            g = Code(eg, max);
            b = Code(eb, max);
        }

        private static ushort Code(double value, int max) => (ushort)Math.Clamp(Math.Floor(value + 0.5), 0, max);
    }
}

/// <summary>The transformative properties (23008-12 6.5.10-6.5.12) on an interleaved RGB canvas.</summary>
internal static class Transforms
{
    /// <summary>
    /// <c>clap</c> (14496-12 12.1.4): a window of the given size centred on the image centre plus the offsets.
    /// The left edge is <c>(width - 1) / 2 + horizOff - (cleanWidth - 1) / 2</c>, rounded to a whole pixel.
    /// </summary>
    public static (ushort[] Canvas, int Width, int Height) Crop(ushort[] canvas, int width, int height, HeifCleanAperture clap)
    {
        var (x0, y0, w, h) = CleanAperture(width, height, clap);

        var cropped = new ushort[(long)w * h * 3];
        for (var y = 0; y < h; y++)
            canvas.AsSpan((int)(((long)(y0 + y) * width + x0) * 3), w * 3).CopyTo(cropped.AsSpan(y * w * 3));
        return (cropped, w, h);
    }

    /// <summary>
    /// The rectangle a <c>clap</c> selects from a <paramref name="width"/> x <paramref name="height"/> image,
    /// checked to lie inside it before anything is cast to int: the fractions are 32-bit numerators over
    /// 32-bit denominators, so a crafted one names a width of 4 billion, which an int cast saturates and an
    /// int sum wraps. Used by <see cref="Crop"/> and by the facade's size, so the two cannot disagree.
    /// </summary>
    /// <exception cref="InvalidDataException">A zero denominator, or a rectangle not inside the image.</exception>
    public static (int X, int Y, int Width, int Height) CleanAperture(int width, int height, HeifCleanAperture clap)
    {
        if (clap.WidthD == 0 || clap.HeightD == 0 || clap.HorizontalOffsetD == 0 || clap.VerticalOffsetD == 0)
            throw new InvalidDataException("'clap' has a zero denominator.");
        var cleanWidth = (double)clap.WidthN / clap.WidthD;
        var cleanHeight = (double)clap.HeightN / clap.HeightD;
        var left = (width - 1) / 2.0 + (double)clap.HorizontalOffsetN / clap.HorizontalOffsetD - (cleanWidth - 1) / 2.0;
        var top = (height - 1) / 2.0 + (double)clap.VerticalOffsetN / clap.VerticalOffsetD - (cleanHeight - 1) / 2.0;
        double x0 = Math.Round(left), y0 = Math.Round(top), w = Math.Round(cleanWidth), h = Math.Round(cleanHeight);
        // In doubles, every term exact at these magnitudes; a NaN fails every comparison and is refused too.
        if (!(w >= 1 && h >= 1 && x0 >= 0 && y0 >= 0 && x0 + w <= width && y0 + h <= height))
            throw new InvalidDataException($"'clap' selects {w}x{h} at ({x0}, {y0}), outside the {width}x{height} image.");
        return ((int)x0, (int)y0, (int)w, (int)h);
    }

    /// <summary><c>irot</c>: <paramref name="degrees"/> ANTICLOCKWISE.</summary>
    public static (ushort[] Canvas, int Width, int Height) Rotate(ushort[] canvas, int width, int height, int degrees)
    {
        if (degrees == 180)
        {
            var pixels = width * height;
            for (int i = 0, j = pixels - 1; i < j; i++, j--)
                for (var c = 0; c < 3; c++)
                    (canvas[3 * i + c], canvas[3 * j + c]) = (canvas[3 * j + c], canvas[3 * i + c]);
            return (canvas, width, height);
        }

        var rotated = new ushort[canvas.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // 90 anticlockwise: (x, y) -> (y, width - 1 - x) in a height x width image; 270 is the other way.
                var (nx, ny) = degrees == 90 ? (y, width - 1 - x) : (height - 1 - y, x);
                var from = 3 * (y * width + x);
                var to = 3 * (ny * height + nx);
                rotated[to] = canvas[from];
                rotated[to + 1] = canvas[from + 1];
                rotated[to + 2] = canvas[from + 2];
            }
        }
        return (rotated, height, width);
    }

    /// <summary><c>imir</c>: <see cref="HeifMirrorAxis.Horizontal"/> (1) swaps left and right, <see cref="HeifMirrorAxis.Vertical"/> (0) top and bottom.</summary>
    public static void Mirror(ushort[] canvas, int width, int height, HeifMirrorAxis axis)
    {
        if (axis == HeifMirrorAxis.Horizontal)
        {
            for (var y = 0; y < height; y++)
                for (int l = 0, r = width - 1; l < r; l++, r--)
                    for (var c = 0; c < 3; c++)
                        (canvas[3 * (y * width + l) + c], canvas[3 * (y * width + r) + c]) = (canvas[3 * (y * width + r) + c], canvas[3 * (y * width + l) + c]);
        }
        else
        {
            var row = width * 3;
            var scratch = new ushort[row];
            for (int t = 0, b = height - 1; t < b; t++, b--)
            {
                canvas.AsSpan(t * row, row).CopyTo(scratch);
                canvas.AsSpan(b * row, row).CopyTo(canvas.AsSpan(t * row, row));
                scratch.CopyTo(canvas.AsSpan(b * row, row));
            }
        }
    }
}
