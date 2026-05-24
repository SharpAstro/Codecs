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

    /// <summary>
    /// INDEX_TABLE_TILES offsets when the codestream carries one, otherwise
    /// null. Spatial mode: one entry per tile. Frequency mode: one entry per
    /// (tile, band) pair, flattened tile-major. Offsets are byte positions
    /// relative to the start of CODED_TILES (i.e. the byte immediately after
    /// PROFILE_LEVEL_INFO).
    /// </summary>
    public long[]? TileOffsets { get; init; }

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

        var freq = ImageHeader.FrequencyModeCodestreamFlag;

        if (!ImageHeader.TilingFlag)
        {
            if (ImageHeader.IndexTablePresentFlag)
            {
                // Single-tile + IndexTable: a degenerate table with one entry per
                // band. WIC's own encoder uses this combination; Windows Photo /
                // WIC's WMPhoto decoder appears to require the table to be
                // present in order to instantiate a frame, even when it carries
                // no useful seek information.
                var perTile = freq ? TileFrequency.BandCount(PlaneHeader.BandsPresent) : 1;

                // Encode the lone tile to a byte buffer first so we can compute
                // the band offsets (always 0 for the first band; subsequent
                // bands offset by the size of the previous band's bytes).
                byte[][] perBandBytes;
                if (freq)
                {
                    perBandBytes = TileFrequency.WriteBands(
                        TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
                        PlaneHeader.BandsPresent,
                        ImageHeader.TrimFlexBitsFlag,
                        PlaneHeader,
                        WidthInMb,
                        HeightInMb,
                        Macroblocks);
                }
                else
                {
                    var tileWriter = new BitWriter();
                    TileSpatial.Write(
                        tileWriter,
                        TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
                        PlaneHeader.BandsPresent,
                        ImageHeader.TrimFlexBitsFlag,
                        PlaneHeader,
                        WidthInMb,
                        HeightInMb,
                        Macroblocks);
                    perBandBytes = [tileWriter.ToArray()];
                }

                var offsets = new long[perBandBytes.Length];
                long running = 0;
                for (var i = 0; i < perBandBytes.Length; i++)
                {
                    offsets[i] = running;
                    running += perBandBytes[i].Length;
                }
                new IndexTableTiles { Offsets = offsets }.Write(writer);

                ProfileLevelInfo.Write(writer);

                foreach (var band in perBandBytes)
                    for (var i = 0; i < band.Length; i++)
                        writer.WriteBits(band[i], 8);

                return writer.ToArray();
            }

            ProfileLevelInfo.Write(writer);
            if (freq)
            {
                TileFrequency.Write(
                    writer,
                    TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
                    PlaneHeader.BandsPresent,
                    ImageHeader.TrimFlexBitsFlag,
                    PlaneHeader,
                    WidthInMb,
                    HeightInMb,
                    Macroblocks);
            }
            else
            {
                TileSpatial.Write(
                    writer,
                    TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
                    PlaneHeader.BandsPresent,
                    ImageHeader.TrimFlexBitsFlag,
                    PlaneHeader,
                    WidthInMb,
                    HeightInMb,
                    Macroblocks);
            }
            return writer.ToArray();
        }

        // Multi-tile: pre-encode each tile to its own byte buffer so we can
        // measure sizes BEFORE emitting INDEX_TABLE_TILES (which needs the
        // offsets), then assemble the final codestream in spec order:
        //   IMAGE_HEADER → IMAGE_PLANE_HEADER → INDEX_TABLE_TILES? →
        //   PROFILE_LEVEL_INFO → CODED_TILES_DATA
        // In frequency mode each tile contributes BandCount byte arrays
        // (one per band sub-stream) — they're flattened tile-major into the
        // index table per T.832 §8.7.1.3.
        var tileBytes = new List<byte[]>();   // spatial: one entry per tile; freq: bandCount per tile
        var tileBounds = ComputeTileBounds(ImageHeader, WidthInMb, HeightInMb).ToList();
        foreach (var (tileX, tileY, tw, th) in tileBounds)
        {
            var tileMbs = SliceTile(Macroblocks, WidthInMb, tileX, tileY, tw, th);
            if (freq)
            {
                var bandBytes = TileFrequency.WriteBands(
                    TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
                    PlaneHeader.BandsPresent,
                    ImageHeader.TrimFlexBitsFlag,
                    PlaneHeader,
                    tw,
                    th,
                    tileMbs);
                foreach (var b in bandBytes) tileBytes.Add(b);
            }
            else
            {
                var tileWriter = new BitWriter();
                TileSpatial.Write(
                    tileWriter,
                    TileBandHeaders.Uniform(PlaneHeader.BandsPresent),
                    PlaneHeader.BandsPresent,
                    ImageHeader.TrimFlexBitsFlag,
                    PlaneHeader,
                    tw,
                    th,
                    tileMbs);
                tileBytes.Add(tileWriter.ToArray());
            }
        }

        if (ImageHeader.IndexTablePresentFlag)
        {
            // Offsets are relative to the start of CODED_TILES (i.e. the first entry is 0).
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

        // Concatenate the pre-encoded tile data. Each sub-stream already ends
        // on a byte boundary thanks to byte-alignment in TileSpatial /
        // TileFrequency, so we can splice without re-bit-aligning.
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

        var plane = ImagePlaneHeader.Read(ref reader, img.OutputBitDepth);

        // INDEX_TABLE_TILES (T.832 §8.7.1.3) sits between IMAGE_PLANE_HEADER and
        // PROFILE_LEVEL_INFO when IndexTablePresentFlag is set. In spatial
        // mode there's one entry per tile; in frequency mode the table has
        // one entry per (tile × band-sub-stream). We don't currently use the
        // offsets for random access — sequential decode works either way —
        // but we must read past the table or the next structure parses garbage.
        long[]? tileOffsets = null;
        if (img.IndexTablePresentFlag)
        {
            var numVerTiles = img.TilingFlag ? img.NumVerTilesMinus1 + 1 : 1;
            var numHorTiles = img.TilingFlag ? img.NumHorTilesMinus1 + 1 : 1;
            var tilesCount = numVerTiles * numHorTiles;
            var perTile = img.FrequencyModeCodestreamFlag
                ? TileFrequency.BandCount(plane.BandsPresent)
                : 1;
            tileOffsets = IndexTableTiles.Read(ref reader, tilesCount * perTile).Offsets;
        }

        var profile = ProfileLevelInfo.Read(ref reader);

        var width = (int)(img.WidthMinus1 + 1);
        var height = (int)(img.HeightMinus1 + 1);
        var widthInMb = (width + 15) >> 4;
        var heightInMb = (height + 15) >> 4;

        var freq = img.FrequencyModeCodestreamFlag;

        Macroblock[] mbs;
        if (!img.TilingFlag)
        {
            mbs = freq
                ? TileFrequency.Read(
                    ref reader,
                    plane.BandsPresent,
                    img.TrimFlexBitsFlag,
                    plane,
                    widthInMb,
                    heightInMb,
                    out _)
                : TileSpatial.Read(
                    ref reader,
                    plane.BandsPresent,
                    img.TrimFlexBitsFlag,
                    plane,
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
                var tileMbs = freq
                    ? TileFrequency.Read(
                        ref reader,
                        plane.BandsPresent,
                        img.TrimFlexBitsFlag,
                        plane,
                        tw,
                        th,
                        out _)
                    : TileSpatial.Read(
                        ref reader,
                        plane.BandsPresent,
                        img.TrimFlexBitsFlag,
                        plane,
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
            TileOffsets = tileOffsets,
        };
    }

    /// <summary>
    /// Random-access decode of a single tile via INDEX_TABLE_TILES. The
    /// codestream prologue (IMAGE_HEADER + IMAGE_PLANE_HEADER +
    /// INDEX_TABLE_TILES + PROFILE_LEVEL_INFO) is parsed unconditionally; the
    /// chosen tile is then seeked-to using the index table's byte offset and
    /// decoded in isolation.
    /// </summary>
    /// <param name="bytes">Full JXR codestream bytes.</param>
    /// <param name="tileX">Tile column index in <c>0..NumVerTilesMinus1</c>.</param>
    /// <param name="tileY">Tile row index in <c>0..NumHorTilesMinus1</c>.</param>
    /// <returns>The macroblocks for the requested tile in tile-raster order;
    /// length is <c>tileWidthInMb × tileHeightInMb</c>.</returns>
    /// <remarks>
    /// Requires <c>TILING_FLAG=true</c> AND <c>INDEX_TABLE_PRESENT_FLAG=true</c>.
    /// Currently spatial-mode only; frequency-mode random access requires
    /// per-band seeking which lands later.
    /// </remarks>
    public static Macroblock[] DecodeTile(ReadOnlySpan<byte> bytes, int tileX, int tileY)
    {
        var reader = new BitReader(bytes);
        var img = ImageHeader.Read(ref reader);
        if (!img.TilingFlag)
            throw new InvalidOperationException(
                "CodedImage.DecodeTile requires TilingFlag = true (no tiles to random-access)");
        if (!img.IndexTablePresentFlag)
            throw new InvalidOperationException(
                "CodedImage.DecodeTile requires IndexTablePresentFlag = true (no seek table)");
        if (img.AlphaImagePlaneFlag)
            throw new NotSupportedException("CodedImage.DecodeTile: alpha plane not yet supported");
        if (img.FrequencyModeCodestreamFlag)
            throw new NotSupportedException(
                "CodedImage.DecodeTile: frequency-mode random access not yet supported " +
                "(needs per-band seeking — use CodedImage.Decode for the full codestream)");

        var numVerTiles = img.NumVerTilesMinus1 + 1;
        var numHorTiles = img.NumHorTilesMinus1 + 1;
        if ((uint)tileX >= (uint)numVerTiles)
            throw new ArgumentOutOfRangeException(nameof(tileX), $"tileX {tileX} out of range [0, {numVerTiles})");
        if ((uint)tileY >= (uint)numHorTiles)
            throw new ArgumentOutOfRangeException(nameof(tileY), $"tileY {tileY} out of range [0, {numHorTiles})");

        var plane = ImagePlaneHeader.Read(ref reader, img.OutputBitDepth);
        var tilesCount = numVerTiles * numHorTiles;
        var indexTable = IndexTableTiles.Read(ref reader, tilesCount);
        _ = ProfileLevelInfo.Read(ref reader);

        // After ProfileLevelInfo, BitReader's position IS the start of
        // CODED_TILES — offsets in the index table are relative to here. The
        // T.832 spec guarantees this junction is byte-aligned.
        if ((reader.BitPosition & 7) != 0)
            throw new InvalidDataException("CODED_TILES does not start on a byte boundary");
        var codedTilesByteStart = reader.BitPosition >> 3;

        var tileIdx = tileY * numVerTiles + tileX;
        var tileByteOffset = codedTilesByteStart + (int)indexTable.Offsets[tileIdx];

        // Resolve tile dimensions in MB-grid coords — TileSpatial.Read needs them.
        var widthInMb = ((int)(img.WidthMinus1 + 1) + 15) >> 4;
        var heightInMb = ((int)(img.HeightMinus1 + 1) + 15) >> 4;
        var bounds = ComputeTileBounds(img, widthInMb, heightInMb).ToList();
        var (_, _, tw, th) = bounds[tileIdx];

        // Seek and decode just this tile.
        reader.SeekToByte(tileByteOffset);
        return TileSpatial.Read(
            ref reader,
            plane.BandsPresent,
            img.TrimFlexBitsFlag,
            plane,
            tw,
            th,
            out _);
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
        if (Macroblocks.Length != WidthInMb * HeightInMb)
            throw new InvalidOperationException(
                $"Macroblocks has length {Macroblocks.Length}, expected {WidthInMb * HeightInMb} " +
                $"({WidthInMb}×{HeightInMb}) for image {Width}×{Height}");
    }
}
