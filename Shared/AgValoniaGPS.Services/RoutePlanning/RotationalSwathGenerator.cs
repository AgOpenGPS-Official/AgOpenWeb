// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.
//
// =============================================================================
// F2C-style swath generator: rotate the cell so the swath direction aligns
// with +x, generate horizontal lines at op-width spacing, intersect each
// against outer + inner rings, then rotate the resulting segments back.
//
// Replaces the existing CellSwathGenerator's monotone-cell-only assumption
// with proper polygon-with-holes clipping; an undrivable inner ring breaks
// each swath that crosses it into multiple sibling segments rather than
// producing a single segment that passes through the obstacle.
// =============================================================================

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

public class RotationalSwathGenerator
{
    /// <summary>
    /// Generate swaths through <paramref name="cell"/> at the given direction.
    /// Inner rings of the cell are treated as undrivable obstacles — swaths
    /// crossing them get split into sibling segments.
    /// </summary>
    /// <param name="cell">Source cell. Polygon = outer ring, InnerRings = holes.</param>
    /// <param name="swathHeading">
    ///   Swath direction in radians (heading from +N CW). Use
    ///   <see cref="BruteForceSwathAngle.FindOptimalHeading(Cell, double, ISwathAngleObjective?, double)"/>
    ///   to pick this; or pass an AB-line heading for a fixed direction.
    /// </param>
    /// <param name="opWidth">
    ///   Effective swath spacing in meters (tool width minus overlap). Must be &gt; 0.
    /// </param>
    public List<GeneratedSwath> Generate(Cell cell, double swathHeading, double opWidth)
    {
        if (cell == null || cell.Polygon == null || cell.Polygon.Count < 3 || opWidth <= 0)
            return new List<GeneratedSwath>();

        // Rotation that takes the swath-direction unit vector (sin h, cos h)
        // to the +x axis. Forward: (E, N) → (E sin h + N cos h, -E cos h + N sin h).
        double sh = Math.Sin(swathHeading);
        double ch = Math.Cos(swathHeading);

        var outerRot = Rotate(cell.Polygon, sh, ch);
        var innersRot = new List<List<Vec2>>(cell.InnerRings?.Count ?? 0);
        if (cell.InnerRings != null)
            foreach (var ring in cell.InnerRings)
                if (ring != null && ring.Count >= 3)
                    innersRot.Add(Rotate(ring, sh, ch));

        // Swaths are horizontal lines (constant y) in the rotated frame.
        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
        foreach (var v in outerRot)
        {
            if (v.Northing < yMin) yMin = v.Northing;
            if (v.Northing > yMax) yMax = v.Northing;
        }

        double extent = yMax - yMin;
        if (extent < opWidth * 0.5)
        {
            // Field strip narrower than half a swath — emit a single centered
            // swath if the centerline lies inside the cell.
            double yMid = 0.5 * (yMin + yMax);
            return GenerateOne(outerRot, innersRot, yMid, swathHeading, sh, ch, index: 0);
        }

        int count = (int)Math.Floor(extent / opWidth);
        if (count <= 0) return new List<GeneratedSwath>();

        var result = new List<GeneratedSwath>(count);
        for (int i = 0; i < count; i++)
        {
            double y = yMin + (i + 0.5) * opWidth;
            result.AddRange(GenerateOne(outerRot, innersRot, y, swathHeading, sh, ch, index: i));
        }
        return result;
    }

    /// <summary>
    /// Build the swath at one perp coordinate. Returns 0 or 1 GeneratedSwaths
    /// (the swath is empty when the line misses the cell entirely).
    /// </summary>
    private static List<GeneratedSwath> GenerateOne(
        List<Vec2> outerRot, List<List<Vec2>> innersRot, double y,
        double swathHeading, double sh, double ch, int index)
    {
        var segmentsRot = ClipHorizontalLine(outerRot, innersRot, y);
        if (segmentsRot.Count == 0) return new List<GeneratedSwath>();

        // Rotate each segment back to original frame.
        var segments = new List<SwathSegment>(segmentsRot.Count);
        foreach (var (x1, x2) in segmentsRot)
            segments.Add(new SwathSegment(Unrotate(x1, y, sh, ch), Unrotate(x2, y, sh, ch)));

        return new List<GeneratedSwath>
        {
            new()
            {
                Index = index,
                SwathHeading = swathHeading,
                PerpCoord = y,
                Segments = segments,
            },
        };
    }

    /// <summary>
    /// Compute the in-cell intervals of a horizontal line y = const, treating
    /// inner rings as exclusion zones. Returns a list of (xLeft, xRight) pairs
    /// in increasing-x order.
    /// </summary>
    private static List<(double xL, double xR)> ClipHorizontalLine(
        List<Vec2> outerRot, List<List<Vec2>> innersRot, double y)
    {
        var crossings = new List<double>();
        AddCrossings(crossings, outerRot, y);
        foreach (var inner in innersRot) AddCrossings(crossings, inner, y);
        if (crossings.Count < 2) return new List<(double, double)>();

        crossings.Sort();

        // Iterate consecutive pairs; keep the pair when the midpoint is inside
        // the polygon-with-holes (inside outer AND outside every inner).
        var result = new List<(double, double)>();
        for (int i = 0; i + 1 < crossings.Count; i++)
        {
            double x1 = crossings[i];
            double x2 = crossings[i + 1];
            // Skip degenerate (zero-length) intervals — they happen when an
            // edge endpoint sits exactly on the line, producing a duplicate.
            if (x2 - x1 < 1e-9) continue;
            double midX = 0.5 * (x1 + x2);
            if (IsInsidePolygonWithHoles(new Vec2(midX, y), outerRot, innersRot))
                result.Add((x1, x2));
        }
        return result;
    }

    private static void AddCrossings(List<double> crossings, List<Vec2> poly, double y)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            double ya = a.Northing, yb = b.Northing;
            // Half-open interval: [min, max) ensures vertex hits are not double-counted.
            bool crossesUp = ya <= y && yb > y;
            bool crossesDown = yb <= y && ya > y;
            if (!crossesUp && !crossesDown) continue;
            double dy = yb - ya;
            if (Math.Abs(dy) < 1e-12) continue;
            double t = (y - ya) / dy;
            crossings.Add(a.Easting + t * (b.Easting - a.Easting));
        }
    }

    private static bool IsInsidePolygonWithHoles(Vec2 p, List<Vec2> outer, List<List<Vec2>> inners)
    {
        if (!GeometryMath.IsPointInPolygon(outer, p)) return false;
        foreach (var inner in inners)
            if (GeometryMath.IsPointInPolygon(inner, p)) return false;
        return true;
    }

    // -- Coordinate transforms --

    private static List<Vec2> Rotate(List<Vec2> pts, double sh, double ch)
    {
        var r = new List<Vec2>(pts.Count);
        // (E, N) → (x = E sh + N ch, y = -E ch + N sh)
        foreach (var p in pts)
            r.Add(new Vec2(p.Easting * sh + p.Northing * ch,
                           -p.Easting * ch + p.Northing * sh));
        return r;
    }

    private static Vec2 Unrotate(double x, double y, double sh, double ch)
    {
        // Inverse: (x, y) → (E = x sh - y ch, N = x ch + y sh)
        return new Vec2(x * sh - y * ch, x * ch + y * sh);
    }
}
