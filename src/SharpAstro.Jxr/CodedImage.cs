namespace SharpAstro.Jxr;

/// <summary>
/// Top-level <c>CODED_IMAGE</c> structure — T.832 §8.2. Wraps the codestream
/// prologue (<see cref="ImageHeader"/>, primary <see cref="ImagePlaneHeader"/>,
/// <see cref="ProfileLevelInfo"/>) around the coded tile data. For
/// single-tile spatial-mode codestreams the body is a single
/// <see cref="TileSpatial"/> block.
/// </summary>
/// <remarks>
/// <para>Current restrictions match the underlying components:</para>
/// <list type="bullet">
///   <item>Single tile only (<c>TILING_FLAG=false</c>) — no
///         <c>INDEX_TABLE_TILES</c>, no <c>SUBSEQUENT_BYTES</c>.</item>
///   <item>No alpha plane (<c>ALPHA_IMAGE_PLANE_FLAG=false</c>).</item>
///   <item>Spatial mode (<c>FREQUENCY_MODE_CODESTREAM_FLAG=false</c>).</item>
///   <item><see cref="JxrBandsPresent.AllBands"/> still rejected by
///         <see cref="TileSpatial"/> (FlexBits refinement is pending).</item>
/// </list>
/// <para>The width/height in pixels comes from <see cref="ImageHeader"/>;
/// the macroblock-grid dimensions are derived as <c>ceil(width/16) ×
/// ceil(height/16)</c>. Bottom-right macroblocks at the image edge are
/// padded to 16×16 by the upstream transform pipeline.</para>
/// </remarks>
public sealed class CodedImage
{
    public required ImageHeader ImageHeader { get; init; }
    public required ImagePlaneHeader PlaneHeader { get; init; }
    public required ProfileLevelInfo ProfileLevelInfo { get; init; }
    public required Macroblock[] Macroblocks { get; init; }

    /// <summary>Pixel width (= <c>ImageHeader.WidthMinus1 + 1</c>).</summary>
    public int Width => (int)(ImageHeader.WidthMinus1 + 1);

    /// <summary>Pixel height (= <c>ImageHeader.HeightMinus1 + 1</c>).</summary>
    public int Height => (int)(ImageHeader.HeightMinus1 + 1);

    /// <summary>Macroblock-grid width — <c>ceil(Width / 16)</c>.</summary>
    public int WidthInMb => (Width + 15) >> 4;

    /// <summary>Macroblock-grid height — <c>ceil(Height / 16)</c>.</summary>
    public int HeightInMb => (Height + 15) >> 4;

    /// <summary>
    /// Serialise the codestream: <c>IMAGE_HEADER → IMAGE_PLANE_HEADER →
    /// PROFILE_LEVEL_INFO → TILE_SPATIAL</c>. Returns the byte sequence
    /// suitable for embedding inside a JXR container (<see cref="JxrContainer"/>).
    /// </summary>
    public byte[] Encode()
    {
        ValidateForEncode();

        var writer = new BitWriter();
        ImageHeader.Write(writer);
        PlaneHeader.Write(writer, ImageHeader.OutputBitDepth);

        if (!ImageHeader.TilingFlag)
        {
            if (ImageHeader.IndexTablePresentFlag)
                throw new NotSupportedException(
                    "IndexTablePresentFlag = true requires TilingFlag = true (single-tile codestreams don't benefit from a seek table)");

            ProfileLevelInfo.Write(writer);
            TileSpatial.Write(
                writer,
                TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
                PlaneHeader.BandsPresent,
                ImageHeader.TrimFlexBitsFlag,
                PlaneHeader.InternalClrFmt,
                PlaneHeader.NumComponents,
                WidthInMb,
                HeightInMb,
                Macroblocks);
            return writer.ToArray();
        }

        // Multi-tile: pre-encode each tile to its own byte buffer so we can
        // measure tile sizes BEFORE emitting INDEX_TABLE_TILES (which needs the
        // offsets), then assemble the final codestream in spec order:
        //   IMAGE_HEADER → IMAGE_PLANE_HEADER → INDEX_TABLE_TILES? →
        //   PROFILE_LEVEL_INFO → CODED_TILES_DATA
        var tileBytes = new List<byte[]>();
        var tileBounds = ComputeTileBounds(ImageHeader, WidthInMb, HeightInMb).ToList();
        foreach (var (tileX, tileY, tw, th) in tileBounds)
        {
            var tileMbs = SliceTile(Macroblocks, WidthInMb, tileX, tileY, tw, th);
            var tileWriter = new BitWriter();
            TileSpatial.Write(
                tileWriter,
                TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
                PlaneHeader.BandsPresent,
                ImageHeader.TrimFlexBitsFlag,
                PlaneHeader.InternalClrFmt,
                PlaneHeader.NumComponents,
                tw,
                th,
                tileMbs);
            tileBytes.Add(tileWriter.ToArray());
        }

        if (ImageHeader.IndexTablePresentFlag)
        {
            // Offsets are relative to the start of CODED_TILES (i.e. the first tile begins at 0).
            var offsets = new long[tileBytes.Count];
            long running = 0;
            for (var i = 0; i < tileBytes.Count; i++)
            {
                offsets[i] = running;
                running += tileBytes[i].Length;
            }
            var indexTable = new IndexTableTiles { Offsets = offsets };
            indexTable.Write(writer);
        }

        ProfileLevelInfo.Write(writer);

        // Concatenate the pre-encoded tile data. Each tile already ends on a
        // byte boundary thanks to TileSpatial's byte_alignment(), so we can
        // splice without re-bit-aligning.
        foreach (var tile in tileBytes)
            for (var i = 0; i < tile.Length; i++)
                writer.WriteBits(tile[i], 8);

        return writer.ToArray();
    }

    /// <summary>Deserialise the codestream produced by <see cref="Encode"/>.</summary>
    public static CodedImage Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new BitReader(bytes);
        var img = ImageHeader.Read(ref reader);
        if (img.AlphaImagePlaneFlag)
            throw new NotSupportedException("CodedImage.Decode: alpha plane not yet supported");
        if (img.FrequencyModeCodestreamFlag)
            throw new NotSupportedException("CodedImage.Decode: frequency-mode codestream not yet supported");

        var plane = ImagePlaneHeader.Read(ref reader, img.OutputBitDepth);

        // INDEX_TABLE_TILES (T.832 §8.7.1.3) sits between IMAGE_PLANE_HEADER and
        // PROFILE_LEVEL_INFO when IndexTablePresentFlag is set. We don't yet
        // exploit the seek offsets — sequential decode works either way — but
        // we must read past the table or the next structure parses garbage.
        if (img.IndexTablePresentFlag)
        {
            var numVerTiles = img.TilingFlag ? img.NumVerTilesMinus1 + 1 : 1;
            var numHorTiles = img.TilingFlag ? img.NumHorTilesMinus1 + 1 : 1;
            var tilesCount = numVerTiles * numHorTiles;
            // Spatial mode: one entry per tile. Frequency mode would multiply
            // by the number of present bands — when we add frequency-mode
            // decode this expectation widens.
            _ = IndexTableTiles.Read(ref reader, tilesCount);
        }

        var profile = ProfileLevelInfo.Read(ref reader);

        var width = (int)(img.WidthMinus1 + 1);
        var height = (int)(img.HeightMinus1 + 1);
        var widthInMb = (width + 15) >> 4;
        var heightInMb = (height + 15) >> 4;

        Macroblock[] mbs;
        if (!img.TilingFlag)
        {
            mbs = TileSpatial.Read(
                ref reader,
                plane.BandsPresent,
                img.TrimFlexBitsFlag,
                plane.InternalClrFmt,
                plane.NumComponents,
                widthInMb,
                heightInMb,
                out _);
        }
        else
        {
            mbs = new Macroblock[widthInMb * heightInMb];
            var tileBounds = ComputeTileBounds(img, widthInMb, heightInMb);
            foreach (var (tileX, tileY, tw, th) in tileBounds)
            {
                var tileMbs = TileSpatial.Read(
                    ref reader,
                    plane.BandsPresent,
                    img.TrimFlexBitsFlag,
                    plane.InternalClrFmt,
                    plane.NumComponents,
                    tw,
                    th,
                    out _);
                Splat(tileMbs, mbs, widthInMb, tileX, tileY, tw, th);
            }
        }

        return new CodedImage
        {
            ImageHeader = img,
            PlaneHeader = plane,
            ProfileLevelInfo = profile,
            Macroblocks = mbs,
        };
    }

    /// <summary>
    /// Walk the tile grid declared in <paramref name="header"/> and yield each
    /// tile's MB-coordinate rectangle <c>(tileX, tileY, widthInMb, heightInMb)</c>
    /// in tile-raster order. The last tile column / row derives its width / height
    /// by subtraction (T.832 8.3.X).
    /// </summary>
    public static IEnumerable<(int tileX, int tileY, int widthInMb, int heightInMb)> ComputeTileBounds(
        ImageHeader header, int totalWidthInMb, int totalHeightInMb)
    {
        // Resolve every tile column's width (with the last derived).
        var numVerTiles = header.NumVerTilesMinus1 + 1;
        var numHorTiles = header.NumHorTilesMinus1 + 1;
        var widths = new int[numVerTiles];
        var heights = new int[numHorTiles];

        var widthSoFar = 0;
        for (var i = 0; i < numVerTiles - 1; i++)
        {
            widths[i] = header.TileWidthInMb[i];
            widthSoFar += widths[i];
        }
        widths[numVerTiles - 1] = totalWidthInMb - widthSoFar;

        var heightSoFar = 0;
        for (var i = 0; i < numHorTiles - 1; i++)
        {
            heights[i] = header.TileHeightInMb[i];
            heightSoFar += heights[i];
        }
        heights[numHorTiles - 1] = totalHeightInMb - heightSoFar;

        if (widths[numVerTiles - 1] <= 0)
            throw new InvalidDataException(
                $"Last tile column has non-positive width {widths[numVerTiles - 1]} — tile widths sum past image width");
        if (heights[numHorTiles - 1] <= 0)
            throw new InvalidDataException(
                $"Last tile row has non-positive height {heights[numHorTiles - 1]} — tile heights sum past image height");

        var tileY = 0;
        for (var row = 0; row < numHorTiles; row++)
        {
            var tileX = 0;
            for (var col = 0; col < numVerTiles; col++)
            {
                yield return (tileX, tileY, widths[col], heights[row]);
                tileX += widths[col];
            }
            tileY += heights[row];
        }
    }

    private static Macroblock[] SliceTile(
        Macroblock[] imageMbs, int imageWidthInMb,
        int tileX, int tileY, int tileWidthInMb, int tileHeightInMb)
    {
        var tile = new Macroblock[tileWidthInMb * tileHeightInMb];
        for (var r = 0; r < tileHeightInMb; r++)
        for (var c = 0; c < tileWidthInMb; c++)
            tile[r * tileWidthInMb + c] = imageMbs[(tileY + r) * imageWidthInMb + (tileX + c)];
        return tile;
    }

    private static void Splat(
        Macroblock[] tileMbs, Macroblock[] imageMbs, int imageWidthInMb,
        int tileX, int tileY, int tileWidthInMb, int tileHeightInMb)
    {
        for (var r = 0; r < tileHeightInMb; r++)
        for (var c = 0; c < tileWidthInMb; c++)
            imageMbs[(tileY + r) * imageWidthInMb + (tileX + c)] = tileMbs[r * tileWidthInMb + c];
    }

    private void ValidateForEncode()
    {
        if (ImageHeader.AlphaImagePlaneFlag)
            throw new NotSupportedException("CodedImage.Encode: alpha plane not yet supported (set AlphaImagePlaneFlag = false)");
        if (ImageHeader.FrequencyModeCodestreamFlag)
            throw new NotSupportedException("CodedImage.Encode: frequency-mode codestream not yet supported");
        if (Macroblocks.Length != WidthInMb * HeightInMb)
            throw new InvalidOperationException(
                $"Macroblocks has length {Macroblocks.Length}, expected {WidthInMb * HeightInMb} " +
                $"({WidthInMb}×{HeightInMb}) for image {Width}×{Height}");
    }
}
