using SharpAstro.VectorGraphics;

namespace SharpAstro.Svg;

/// <summary>
/// Path data (<c>d</c>) and the basic shapes, all reduced to absolute move / line / quad / cubic / close.
/// The arithmetic is in doubles and narrowed to float only on the way into the builder.
/// </summary>
internal static class SvgGeometry
{
    /// <summary>The control-point distance, over the radius, that makes a cubic follow a quarter circle: 4/3 (sqrt 2 - 1).</summary>
    private const double Kappa = 0.5522847498307936;

    /// <summary>
    /// Parses path data. Returns false when the data has an error; what came before the error is in the builder, which is
    /// the specification's rule ("render up to the last correct segment").
    /// </summary>
    public static bool ParsePath(ReadOnlySpan<char> data, PathBuilder sink)
    {
        var s = new SvgScanner(data);
        double cx = 0, cy = 0;      // current point
        double sx = 0, sy = 0;      // start of the current subpath
        double kx = 0, ky = 0;      // last control point, for S and T
        char command = '\0';        // the command in force, as written
        char previous = '\0';       // the command before, upper case
        bool open = false;          // a Move has been emitted for the current subpath
        bool any = false;

        s.SkipWhitespace();
        while (!s.AtEnd)
        {
            char c = s.Peek;
            if (char.IsAsciiLetter(c))
            {
                if ("MmZzLlHhVvCcSsQqTtAa".IndexOf(c) < 0) return false;
                if (!any && c is not ('M' or 'm')) return false;
                command = c;
                s.Advance();
            }
            else if (command is '\0' or 'Z' or 'z')
            {
                return false;
            }
            else if (command == 'M')
            {
                command = 'L';   // coordinates after the first pair of a moveto are linetos
            }
            else if (command == 'm')
            {
                command = 'l';
            }
            any = true;

            bool relative = char.IsAsciiLetterLower(command);
            char upper = char.ToUpperInvariant(command);
            double ox = relative ? cx : 0, oy = relative ? cy : 0;
            double x, y, x1, y1, x2, y2;
            switch (upper)
            {
                case 'Z':
                    if (open) sink.Close();
                    open = false;
                    cx = sx;
                    cy = sy;
                    break;

                case 'M':
                    if (!Number(ref s, true, out x) || !Number(ref s, false, out y)) return false;
                    cx = sx = x + ox;
                    cy = sy = y + oy;
                    sink.MoveTo((float)cx, (float)cy);
                    open = true;
                    break;

                case 'L':
                    if (!Number(ref s, true, out x) || !Number(ref s, false, out y)) return false;
                    EnsureOpen(sink, ref open, sx, sy);
                    cx = x + ox;
                    cy = y + oy;
                    sink.LineTo((float)cx, (float)cy);
                    break;

                case 'H':
                    if (!Number(ref s, true, out x)) return false;
                    EnsureOpen(sink, ref open, sx, sy);
                    cx = x + ox;
                    sink.LineTo((float)cx, (float)cy);
                    break;

                case 'V':
                    if (!Number(ref s, true, out y)) return false;
                    EnsureOpen(sink, ref open, sx, sy);
                    cy = y + oy;
                    sink.LineTo((float)cx, (float)cy);
                    break;

                case 'C':
                    if (!Number(ref s, true, out x1) || !Number(ref s, false, out y1) ||
                        !Number(ref s, false, out x2) || !Number(ref s, false, out y2) ||
                        !Number(ref s, false, out x) || !Number(ref s, false, out y)) return false;
                    EnsureOpen(sink, ref open, sx, sy);
                    kx = x2 + ox;
                    ky = y2 + oy;
                    sink.CubicTo((float)(x1 + ox), (float)(y1 + oy), (float)kx, (float)ky, (float)(x + ox), (float)(y + oy));
                    cx = x + ox;
                    cy = y + oy;
                    break;

                case 'S':
                    if (!Number(ref s, true, out x2) || !Number(ref s, false, out y2) ||
                        !Number(ref s, false, out x) || !Number(ref s, false, out y)) return false;
                    EnsureOpen(sink, ref open, sx, sy);
                    // The first control point mirrors the previous curve's second one, or is the current point.
                    x1 = previous is 'C' or 'S' ? 2 * cx - kx : cx;
                    y1 = previous is 'C' or 'S' ? 2 * cy - ky : cy;
                    kx = x2 + ox;
                    ky = y2 + oy;
                    sink.CubicTo((float)x1, (float)y1, (float)kx, (float)ky, (float)(x + ox), (float)(y + oy));
                    cx = x + ox;
                    cy = y + oy;
                    break;

                case 'Q':
                    if (!Number(ref s, true, out x1) || !Number(ref s, false, out y1) ||
                        !Number(ref s, false, out x) || !Number(ref s, false, out y)) return false;
                    EnsureOpen(sink, ref open, sx, sy);
                    kx = x1 + ox;
                    ky = y1 + oy;
                    sink.QuadTo((float)kx, (float)ky, (float)(x + ox), (float)(y + oy));
                    cx = x + ox;
                    cy = y + oy;
                    break;

                case 'T':
                    if (!Number(ref s, true, out x) || !Number(ref s, false, out y)) return false;
                    EnsureOpen(sink, ref open, sx, sy);
                    kx = previous is 'Q' or 'T' ? 2 * cx - kx : cx;
                    ky = previous is 'Q' or 'T' ? 2 * cy - ky : cy;
                    sink.QuadTo((float)kx, (float)ky, (float)(x + ox), (float)(y + oy));
                    cx = x + ox;
                    cy = y + oy;
                    break;

                default: // 'A'
                    if (!Number(ref s, true, out double rx) || !Number(ref s, false, out double ry) ||
                        !Number(ref s, false, out double rotation)) return false;
                    s.SkipCommaWhitespace();
                    if (!s.TryFlag(out bool large)) return false;
                    s.SkipCommaWhitespace();
                    if (!s.TryFlag(out bool sweep)) return false;
                    if (!Number(ref s, false, out x) || !Number(ref s, false, out y)) return false;
                    EnsureOpen(sink, ref open, sx, sy);
                    Arc(sink, cx, cy, rx, ry, rotation, large, sweep, x + ox, y + oy);
                    cx = x + ox;
                    cy = y + oy;
                    break;
            }

            previous = upper;
            s.SkipCommaWhitespace();
        }
        return true;
    }

    private static bool Number(ref SvgScanner s, bool first, out double value)
    {
        if (first) s.SkipWhitespace();
        else s.SkipCommaWhitespace();
        return s.TryNumber(out value);
    }

    /// <summary>After a closepath, a drawing command starts a new subpath at the old one's start; say so to the sink.</summary>
    private static void EnsureOpen(PathBuilder sink, ref bool open, double sx, double sy)
    {
        if (open) return;
        sink.MoveTo((float)sx, (float)sy);
        open = true;
    }

    /// <summary>
    /// An elliptical arc from (x0, y0) to (x, y) as cubics of at most a quarter turn each, by the endpoint-to-centre
    /// conversion of the SVG implementation notes (out-of-range radii are scaled up, a zero radius is a straight line,
    /// coincident end points draw nothing).
    /// </summary>
    internal static void Arc(PathBuilder sink, double x0, double y0, double rx, double ry, double rotationDegrees, bool large, bool sweep, double x, double y)
    {
        if (x0 == x && y0 == y) return;
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            sink.LineTo((float)x, (float)y);
            return;
        }

        (double sinPhi, double cosPhi) = Math.SinCos(rotationDegrees % 360.0 * Math.PI / 180.0);
        double dx = (x0 - x) / 2, dy = (y0 - y) / 2;
        double x1p = cosPhi * dx + sinPhi * dy;
        double y1p = -sinPhi * dx + cosPhi * dy;

        double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1)
        {
            double grow = Math.Sqrt(lambda);
            rx *= grow;
            ry *= grow;
        }

        double rx2 = rx * rx, ry2 = ry * ry;
        double denominator = rx2 * y1p * y1p + ry2 * x1p * x1p;
        double factor = Math.Sqrt(Math.Max(0, (rx2 * ry2 - denominator) / denominator));
        if (large == sweep) factor = -factor;
        double cxp = factor * rx * y1p / ry;
        double cyp = -factor * ry * x1p / rx;
        double centreX = cosPhi * cxp - sinPhi * cyp + (x0 + x) / 2;
        double centreY = sinPhi * cxp + cosPhi * cyp + (y0 + y) / 2;

        double start = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
        double end = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);
        double delta = end - start;
        if (sweep && delta < 0) delta += 2 * Math.PI;
        else if (!sweep && delta > 0) delta -= 2 * Math.PI;

        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        double step = delta / pieces;
        double t = 4.0 / 3.0 * Math.Tan(step / 4);
        double px = x0, py = y0;
        for (int i = 0; i < pieces; i++)
        {
            double a = start + i * step, b = a + step;
            (double sinA, double cosA) = Math.SinCos(a);
            (double sinB, double cosB) = Math.SinCos(b);
            // The last piece ends on the given end point exactly, so rounding does not open a gap before the next segment.
            double qx = i == pieces - 1 ? x : centreX + cosPhi * rx * cosB - sinPhi * ry * sinB;
            double qy = i == pieces - 1 ? y : centreY + sinPhi * rx * cosB + cosPhi * ry * sinB;
            // The ellipse's derivative with respect to its angle, at each end.
            double dax = -cosPhi * rx * sinA - sinPhi * ry * cosA, day = -sinPhi * rx * sinA + cosPhi * ry * cosA;
            double dbx = -cosPhi * rx * sinB - sinPhi * ry * cosB, dby = -sinPhi * rx * sinB + cosPhi * ry * cosB;
            sink.CubicTo((float)(px + t * dax), (float)(py + t * day), (float)(qx - t * dbx), (float)(qy - t * dby), (float)qx, (float)qy);
            px = qx;
            py = qy;
        }
    }

    /// <summary>A rectangle, with the corner radii already clamped to half the sides. Zero-length sides are left out.</summary>
    public static void Rectangle(PathBuilder sink, double x, double y, double w, double h, double rx, double ry)
    {
        if (rx <= 0 || ry <= 0)
        {
            sink.MoveTo((float)x, (float)y);
            sink.LineTo((float)(x + w), (float)y);
            sink.LineTo((float)(x + w), (float)(y + h));
            sink.LineTo((float)x, (float)(y + h));
            sink.Close();
            return;
        }

        double kx = Kappa * rx, ky = Kappa * ry;
        double r = x + w, b = y + h;
        sink.MoveTo((float)(x + rx), (float)y);
        if (w > 2 * rx) sink.LineTo((float)(r - rx), (float)y);
        sink.CubicTo((float)(r - rx + kx), (float)y, (float)r, (float)(y + ry - ky), (float)r, (float)(y + ry));
        if (h > 2 * ry) sink.LineTo((float)r, (float)(b - ry));
        sink.CubicTo((float)r, (float)(b - ry + ky), (float)(r - rx + kx), (float)b, (float)(r - rx), (float)b);
        if (w > 2 * rx) sink.LineTo((float)(x + rx), (float)b);
        sink.CubicTo((float)(x + rx - kx), (float)b, (float)x, (float)(b - ry + ky), (float)x, (float)(b - ry));
        if (h > 2 * ry) sink.LineTo((float)x, (float)(y + ry));
        sink.CubicTo((float)x, (float)(y + ry - ky), (float)(x + rx - kx), (float)y, (float)(x + rx), (float)y);
        sink.Close();
    }

    /// <summary>An ellipse as four quarter arcs, starting at the rightmost point and going by the bottom (SVG 2's order).</summary>
    public static void Ellipse(PathBuilder sink, double cx, double cy, double rx, double ry)
    {
        double kx = Kappa * rx, ky = Kappa * ry;
        sink.MoveTo((float)(cx + rx), (float)cy);
        sink.CubicTo((float)(cx + rx), (float)(cy + ky), (float)(cx + kx), (float)(cy + ry), (float)cx, (float)(cy + ry));
        sink.CubicTo((float)(cx - kx), (float)(cy + ry), (float)(cx - rx), (float)(cy + ky), (float)(cx - rx), (float)cy);
        sink.CubicTo((float)(cx - rx), (float)(cy - ky), (float)(cx - kx), (float)(cy - ry), (float)cx, (float)(cy - ry));
        sink.CubicTo((float)(cx + kx), (float)(cy - ry), (float)(cx + rx), (float)(cy - ky), (float)(cx + rx), (float)cy);
        sink.Close();
    }

    /// <summary>
    /// A <c>points</c> list as a polyline. Returns false when the list has an error (a bad number or an odd count); the
    /// complete pairs before it are drawn, as the specification asks.
    /// </summary>
    public static bool Polyline(ReadOnlySpan<char> points, bool close, PathBuilder sink)
    {
        var s = new SvgScanner(points);
        bool ok = true;
        int count = 0;
        s.SkipWhitespace();
        while (!s.AtEnd)
        {
            if (!s.TryNumber(out double x)) { ok = false; break; }
            s.SkipCommaWhitespace();
            if (!s.TryNumber(out double y)) { ok = false; break; }
            s.SkipCommaWhitespace();
            if (count == 0) sink.MoveTo((float)x, (float)y);
            else sink.LineTo((float)x, (float)y);
            count++;
        }
        if (close && count > 1) sink.Close();
        return ok;
    }
}
