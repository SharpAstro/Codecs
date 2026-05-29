namespace SharpAstro.Jxr;

/// <summary>
/// Walks the QP inheritance chain from <see cref="ImagePlaneHeader"/> down
/// through each <see cref="TileBandHeaders"/> to produce per-MB-per-component
/// quantization values for DC / LP / HP bands. The result feeds straight
/// into <see cref="JxrQuant"/>'s dequantization step so non-uniform-QP
/// codestreams reconstruct with the correct per-MB scale.
/// </summary>
/// <remarks>
/// Per T.832 §8.4 / §8.6, QPs resolve in this priority:
/// <list type="number">
///   <item>If the plane's <c>*_IMAGE_PLANE_UNIFORM_FLAG</c> is true, the
///         plane-level <c>Plane*Qp</c> row applies to every MB.</item>
///   <item>Otherwise, each tile carries its own table. LP can inherit DC's
///         table via <see cref="TileHeaderLowpass.UseDcQpForLp"/>; HP can
///         inherit LP's via <see cref="TileHeaderHighpass.UseLpQpForHp"/>.</item>
///   <item>The per-MB index <see cref="Macroblock.LpQpIndex"/> /
///         <see cref="Macroblock.HpQpIndex"/> selects the row from whichever
///         table applies (DC has no per-MB index, always row 0).</item>
/// </list>
/// </remarks>
internal static class QpResolver
{
    /// <summary>
    /// Build a <c>qp[mbX, mbY, component]</c> divisor grid for one band.
    /// Returns values ready to feed <see cref="JxrQuant.QpIndexToDivisor"/>
    /// — already converted, no further QP→divisor mapping needed by callers.
    /// </summary>
    public static int[,,] BuildBandDivisors(
        CodedImage img,
        QpBand band)
    {
        var mbW = img.WidthInMb;
        var mbH = img.HeightInMb;
        var numComponents = img.PlaneHeader.NumComponents;
        var grid = new int[mbW, mbH, numComponents];

        // Default fallback: plane-level uniform QP (the case our older encoder
        // exclusively used) — equivalent to the legacy single-divisor path.
        var defaultQp = band switch
        {
            QpBand.Dc => img.PlaneHeader.DcQuant,
            QpBand.Lp => img.PlaneHeader.LpQuant,
            QpBand.Hp => img.PlaneHeader.HpQuant,
            _ => throw new ArgumentOutOfRangeException(nameof(band)),
        };
        for (var y = 0; y < mbH; y++)
        for (var x = 0; x < mbW; x++)
        for (var c = 0; c < numComponents; c++)
            grid[x, y, c] = JxrQuant.QpIndexToDivisor(defaultQp);

        // No per-tile headers? Stick with the uniform fallback.
        if (img.PerTileBandHeaders is null || img.TileGridBounds is null)
            return grid;

        // Iterate tiles and overwrite the grid region for each tile with its
        // resolved per-component (and for LP/HP, per-MB) divisors.
        for (var t = 0; t < img.PerTileBandHeaders.Length; t++)
        {
            var (tileX, tileY, tw, th) = img.TileGridBounds[t];
            var headers = img.PerTileBandHeaders[t];

            // Resolve the QpTable that applies inside THIS tile for this band.
            QpTable? table = ResolveTable(img.PlaneHeader, headers, band);
            if (table is null) continue; // uniform fallback already filled this region

            for (var r = 0; r < th; r++)
            for (var c = 0; c < tw; c++)
            {
                var imgX = tileX + c;
                var imgY = tileY + r;
                var mb = img.Macroblocks[imgY * mbW + imgX];
                var row = band switch
                {
                    QpBand.Dc => 0,                 // DC has no per-MB index
                    QpBand.Lp => mb.LpQpIndex,
                    QpBand.Hp => mb.HpQpIndex,
                    _ => 0,
                };
                if ((uint)row >= (uint)table.NumQPs) row = 0;
                for (var comp = 0; comp < numComponents; comp++)
                    grid[imgX, imgY, comp] = JxrQuant.QpIndexToDivisor(table[row, comp]);
            }
        }

        return grid;
    }

    /// <summary>
    /// Resolve which <see cref="QpTable"/> applies for a given (plane, tile,
    /// band) — walking the inheritance chain. Returns null when no per-tile
    /// table applies (caller falls back to the uniform plane-level QP).
    /// </summary>
    private static QpTable? ResolveTable(ImagePlaneHeader plane, TileBandHeaders headers, QpBand band)
    {
        switch (band)
        {
            case QpBand.Dc:
                if (plane.DcImagePlaneUniformFlag) return plane.PlaneDcQp;
                return headers.Dc.DcQp;

            case QpBand.Lp:
                if (plane.LpImagePlaneUniformFlag) return plane.PlaneLpQp;
                var lp = headers.Lowpass;
                if (lp is null) return null;
                if (lp.UseDcQpForLp) return headers.Dc.DcQp ?? plane.PlaneDcQp;
                return lp.LpQp;

            case QpBand.Hp:
                if (plane.HpImagePlaneUniformFlag) return plane.PlaneHpQp;
                var hp = headers.Highpass;
                if (hp is null) return null;
                if (hp.UseLpQpForHp)
                {
                    var lp2 = headers.Lowpass;
                    if (lp2 is null) return null;
                    if (lp2.UseDcQpForLp) return headers.Dc.DcQp ?? plane.PlaneDcQp;
                    return lp2.LpQp;
                }
                return hp.HpQp;

            default:
                throw new ArgumentOutOfRangeException(nameof(band));
        }
    }
}

/// <summary>Which JXR sub-band's QPs are being resolved.</summary>
internal enum QpBand { Dc, Lp, Hp }
