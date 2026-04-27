// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.
//
// =============================================================================
// F2C-style cellular decomposition by raycasting from polygon vertices.
//
// Algorithm:
//   1. Rotate everything so sweep direction lies along +x.
//   2. Pick "split vertices" (every vertex for trapezoidal; only critical ones
//      for boustrophedon) and cast rays in ±y from each.
//   3. Each ray's first interior hit on a polygon edge defines one cell-cutting
//      segment.
//   4. Subtract thin rectangles around each cut from the polygon-with-holes
//      using Clipper2, walk the resulting PolyTree to extract cells.
//   5. Rotate cell polygons back into the original frame.
//
// This file implements both trapezoidal (every-vertex) and boustrophedon
// (critical-vertex-only) variants on the same internal pipeline. Outputs
// follow the plan's polygon-with-holes Cell model — local obstacles are
// attached separately via <see cref="LocalObstacleAttacher"/>.
// =============================================================================

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using Clipper2Lib;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Trapezoidal cellular decomposition: cast rays from EVERY polygon vertex,
/// produce one cell per resulting trapezoid. Generally more cells than the
/// boustrophedon variant; useful as a baseline / comparison.
/// </summary>
public static class TrapezoidalDecomp
{
    public static List<Cell> Decompose(
        List<Vec2> outer,
        List<List<Vec2>> topologicalInners,
        double sweepHeading,
        double decompositionThresholdDegrees = 180.0)
        => RaycastDecomp.Run(outer, topologicalInners, sweepHeading,
            criticalOnly: false, decompositionThresholdDegrees);
}

/// <summary>
/// Boustrophedon cellular decomposition (Choset 2000): only cast rays from
/// critical vertices — vertices where both adjacent edges go the same direction
/// in the sweep coordinate. Produces strictly fewer cells than trapezoidal.
/// Pass <paramref name="decompositionThresholdDegrees"/> &gt; 180 to filter out
/// marginal reflex vertices created by GPS-recorded boundary jitter (Versleijen
/// 2019 §2.2.2; default 200° preserves the decomposition while ignoring
/// near-straight kinks).
/// </summary>
public static class BoustrophedonDecomp
{
    public static List<Cell> Decompose(
        List<Vec2> outer,
        List<List<Vec2>> topologicalInners,
        double sweepHeading,
        double decompositionThresholdDegrees = 180.0)
        => RaycastDecomp.Run(outer, topologicalInners, sweepHeading,
            criticalOnly: true, decompositionThresholdDegrees);
}

/// <summary>
/// Shared pipeline behind <see cref="TrapezoidalDecomp"/> and
/// <see cref="BoustrophedonDecomp"/>. Differ only in the vertex-selection rule
/// for raycasting.
/// </summary>
internal static class RaycastDecomp
{
    // Clipper2 uses 64-bit ints; pick precision so 1mm rounds cleanly.
    private const double Scale = 1e4;          // 1 unit = 0.1 mm
    private const double CutInflation = 1e-3;  // 1 mm thin rectangle around each cut
    // Each cut rectangle is extended past both endpoints by this much. Without
    // the extension, Clipper sees the rectangle sharing edges/vertices with the
    // polygon and produces a slit (one polygon with a thin notch) instead of a
    // clean split into two polygons.
    private const double CutEndExtension = 1e-2;
    private const double InteriorEps = 1e-6;
    private const double SamplingEps = 1e-3;   // step for "is the cut inside the field" probe

    /// <summary>
    /// Run the decomposition. Returns a list of cells with their polygons in
    /// the original (un-rotated) frame.
    /// </summary>
    public static List<Cell> Run(
        List<Vec2> outer,
        List<List<Vec2>> topologicalInners,
        double sweepHeading,
        bool criticalOnly,
        double decompositionThresholdDegrees = 180.0)
    {
        if (outer == null || outer.Count < 3) return new List<Cell>();

        // 1. Rotate to sweep=+x. Original frame: (E, N). Rotated frame: (s, p)
        //    where s is sweep coord, p is perpendicular ("up" in the rotated diagram).
        double sx = Math.Sin(sweepHeading);
        double sy = Math.Cos(sweepHeading);

        // Avoid degenerate (edge-parallel-to-sweep) configurations by jittering.
        sweepHeading = CriticalPointClassifier.AdjustSweepForNonDegenerateOrder(
            sweepHeading,
            EnsureCcw(outer),
            EnsureAllCw(topologicalInners ?? new List<List<Vec2>>()));
        sx = Math.Sin(sweepHeading);
        sy = Math.Cos(sweepHeading);

        var outerRot = EnsureCcw(Rotate(outer, sx, sy));
        var innersRot = new List<List<Vec2>>();
        if (topologicalInners != null)
        {
            foreach (var inner in topologicalInners)
            {
                if (inner == null || inner.Count < 3) continue;
                innersRot.Add(EnsureCw(Rotate(inner, sx, sy)));
            }
        }

        // 2. Collect cuts. For each chosen vertex, cast +y and −y rays; keep
        //    those whose midpoint lies inside the polygon-with-holes.
        var allPolygons = new List<List<Vec2>> { outerRot };
        allPolygons.AddRange(innersRot);

        var cuts = new List<(Vec2 a, Vec2 b)>();
        CollectCuts(outerRot, isHole: false, criticalOnly, decompositionThresholdDegrees, allPolygons, cuts);
        foreach (var inner in innersRot)
            CollectCuts(inner, isHole: true, criticalOnly, decompositionThresholdDegrees, allPolygons, cuts);

        // 3. Apply cuts via Clipper2. Build the polygon-with-holes "field"
        //    and a thin rectangle for each cut, then take field − ⋃cuts.
        var field = new Paths64();
        field.Add(ToPath64(outerRot));
        foreach (var inner in innersRot) field.Add(ToPath64(inner));

        Paths64 result;
        if (cuts.Count == 0)
        {
            result = field;
        }
        else
        {
            var clipPaths = new Paths64();
            foreach (var (a, b) in cuts)
            {
                var rect = ThinRectangle(a, b, CutInflation);
                if (rect != null) clipPaths.Add(rect);
            }

            var clipper = new Clipper64();
            clipper.AddSubject(field);
            clipper.AddClip(clipPaths);
            result = new Paths64();
            clipper.Execute(ClipType.Difference, FillRule.NonZero, result);
        }

        // 4. Walk PolyTree-equivalent: identify each outer (positive area) and
        //    its child holes (negative area, contained inside).
        var cells = AssembleCells(result, sx, sy);

        return cells;
    }

    /// <summary>
    /// Add one cut per vertex × ray-direction (±y in rotated frame) that has
    /// both endpoints on the polygon-with-holes boundary AND a midpoint
    /// strictly inside the field.
    /// </summary>
    private static void CollectCuts(
        List<Vec2> polygon, bool isHole, bool criticalOnly, double decompositionThresholdDegrees,
        List<List<Vec2>> allPolygons, List<(Vec2 a, Vec2 b)> cuts)
    {
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            var v = polygon[i];
            var prev = polygon[(i - 1 + n) % n];
            var next = polygon[(i + 1) % n];

            if (criticalOnly && !IsCritical(prev, v, next)) continue;

            // Versleijen 2019 §2.2.2 threshold: skip vertices whose field-side
            // interior angle is below the threshold (= near-straight reflex
            // bumps from boundary jitter). Default 180° = no extra filtering;
            // 200° = ignore reflexes shallower than 20° beyond straight.
            if (decompositionThresholdDegrees > 180.0)
            {
                double fieldInteriorDeg = FieldInteriorAngleDegrees(prev, v, next, isHole);
                if (fieldInteriorDeg < decompositionThresholdDegrees) continue;
            }

            // +y ray
            var hitUp = Raycast.FirstHit(v, new Vec2(0, 1), allPolygons);
            if (hitUp.HasValue && IsInsideField(Midpoint(v, hitUp.Value), allPolygons[0],
                allPolygons.Count > 1 ? allPolygons.GetRange(1, allPolygons.Count - 1) : null))
                cuts.Add((v, hitUp.Value));

            // −y ray
            var hitDn = Raycast.FirstHit(v, new Vec2(0, -1), allPolygons);
            if (hitDn.HasValue && IsInsideField(Midpoint(v, hitDn.Value), allPolygons[0],
                allPolygons.Count > 1 ? allPolygons.GetRange(1, allPolygons.Count - 1) : null))
                cuts.Add((v, hitDn.Value));
        }
    }

    private static bool IsCritical(Vec2 prev, Vec2 v, Vec2 next)
    {
        // Both adjacent edges go the same direction in sweep (x in rotated frame).
        bool prevForward = prev.Easting > v.Easting + InteriorEps;
        bool nextForward = next.Easting > v.Easting + InteriorEps;
        bool prevBackward = prev.Easting < v.Easting - InteriorEps;
        bool nextBackward = next.Easting < v.Easting - InteriorEps;
        return (prevForward && nextForward) || (prevBackward && nextBackward);
    }

    /// <summary>
    /// Interior angle of the FIELD at vertex v, in degrees. For an outer-CCW
    /// vertex this equals the polygon's interior angle. For a hole-CW vertex,
    /// the field is on the polygon's exterior side, so we return 360°−polygon-interior.
    /// Reflex (concave from the field's perspective) ⇒ &gt; 180°.
    /// </summary>
    private static double FieldInteriorAngleDegrees(Vec2 prev, Vec2 v, Vec2 next, bool isHole)
    {
        // Incoming edge direction = v − prev; outgoing edge direction = next − v.
        double ix = v.Easting - prev.Easting;
        double iy = v.Northing - prev.Northing;
        double ox = next.Easting - v.Easting;
        double oy = next.Northing - v.Northing;
        double cross = ix * oy - iy * ox;
        double dot = ix * ox + iy * oy;
        // atan2 returns the signed turn angle from incoming to outgoing in [−π, π].
        double turn = Math.Atan2(cross, dot);
        // Polygon interior angle (assuming CCW polygon): π − turn.
        double polyInterior = Math.PI - turn;
        if (polyInterior < 0) polyInterior += 2 * Math.PI;
        if (polyInterior > 2 * Math.PI) polyInterior -= 2 * Math.PI;
        // For a hole (CW), the field is on the OPPOSITE side, so flip.
        double fieldInterior = isHole ? (2 * Math.PI - polyInterior) : polyInterior;
        return fieldInterior * 180.0 / Math.PI;
    }

    private static Vec2 Midpoint(Vec2 a, Vec2 b)
        => new(0.5 * (a.Easting + b.Easting), 0.5 * (a.Northing + b.Northing));

    private static bool IsInsideField(Vec2 p, List<Vec2> outer, List<List<Vec2>>? holes)
    {
        if (!GeometryMath.IsPointInPolygon(outer, p)) return false;
        if (holes == null) return true;
        foreach (var h in holes)
            if (GeometryMath.IsPointInPolygon(h, p)) return false;
        return true;
    }

    /// <summary>
    /// Build the four corners of a rectangle of width <paramref name="width"/>
    /// centered on the segment a–b, extended by <see cref="CutEndExtension"/>
    /// past each endpoint along the segment direction.
    /// </summary>
    private static Path64? ThinRectangle(Vec2 a, Vec2 b, double width)
    {
        double dx = b.Easting - a.Easting;
        double dy = b.Northing - a.Northing;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return null;
        double ux = dx / len, uy = dy / len;       // along-segment unit vector
        double nx = -uy * width * 0.5;             // perpendicular half-width
        double ny = ux * width * 0.5;
        double ax = a.Easting - ux * CutEndExtension;
        double ay = a.Northing - uy * CutEndExtension;
        double bx = b.Easting + ux * CutEndExtension;
        double by = b.Northing + uy * CutEndExtension;
        var p = new Path64(4);
        p.Add(ToPoint64(ax + nx, ay + ny));
        p.Add(ToPoint64(bx + nx, by + ny));
        p.Add(ToPoint64(bx - nx, by - ny));
        p.Add(ToPoint64(ax - nx, ay - ny));
        return p;
    }

    /// <summary>
    /// From the Clipper Difference output (a flat list of paths whose sign
    /// distinguishes outer rings from holes), assemble Cells. Each positive-area
    /// path becomes a Cell.Polygon; each negative-area path that lies inside it
    /// becomes one of its InnerRings.
    /// </summary>
    private static List<Cell> AssembleCells(Paths64 paths, double sx, double sy)
    {
        var outers = new List<List<Vec2>>();
        var holes = new List<List<Vec2>>();
        foreach (var p in paths)
        {
            if (p.Count < 3) continue;
            var v = FromPath64(p);
            if (Clipper.Area(p) > 0) outers.Add(v);
            else holes.Add(v);
        }

        var cells = new List<Cell>();
        for (int i = 0; i < outers.Count; i++)
        {
            var outerRot = outers[i];
            var cellHolesRot = new List<List<Vec2>>();
            // A hole belongs to the outer that contains its centroid.
            foreach (var h in holes)
            {
                if (GeometryMath.IsPointInPolygon(outerRot, Centroid(h)))
                    cellHolesRot.Add(h);
            }

            // Rotate back into original frame.
            var outerOrig = Unrotate(outerRot, sx, sy);
            var cellHolesOrig = new List<List<Vec2>>();
            foreach (var h in cellHolesRot) cellHolesOrig.Add(Unrotate(h, sx, sy));

            // Sweep-coord extents (in original frame, projected on sweep direction).
            double minS = double.PositiveInfinity, maxS = double.NegativeInfinity;
            foreach (var pt in outerOrig)
            {
                double s = pt.Easting * sx + pt.Northing * sy;
                if (s < minS) minS = s;
                if (s > maxS) maxS = s;
            }

            cells.Add(new Cell
            {
                Id = i,
                Polygon = outerOrig,
                InnerRings = cellHolesOrig,
                SweepStart = minS,
                SweepEnd = maxS,
            });
        }

        // Sort by sweep start for stable ids / nicer downstream behavior.
        cells.Sort((c1, c2) => c1.SweepStart.CompareTo(c2.SweepStart));
        for (int i = 0; i < cells.Count; i++) cells[i].Id = i;

        return cells;
    }

    private static Vec2 Centroid(List<Vec2> poly)
    {
        double cx = 0, cy = 0;
        foreach (var p in poly) { cx += p.Easting; cy += p.Northing; }
        return new Vec2(cx / poly.Count, cy / poly.Count);
    }

    // -- Coordinate conversion --------------------------------------------------

    private static List<Vec2> Rotate(List<Vec2> pts, double sx, double sy)
    {
        var r = new List<Vec2>(pts.Count);
        foreach (var p in pts)
        {
            // (s, perp) = (E·sx + N·sy,  −E·sy + N·sx)
            r.Add(new Vec2(p.Easting * sx + p.Northing * sy, -p.Easting * sy + p.Northing * sx));
        }
        return r;
    }

    private static List<Vec2> Unrotate(List<Vec2> pts, double sx, double sy)
    {
        var r = new List<Vec2>(pts.Count);
        foreach (var p in pts)
        {
            // (E, N) = (s·sx − perp·sy,  s·sy + perp·sx)
            r.Add(new Vec2(p.Easting * sx - p.Northing * sy, p.Easting * sy + p.Northing * sx));
        }
        return r;
    }

    private static Path64 ToPath64(List<Vec2> poly)
    {
        var p = new Path64(poly.Count);
        foreach (var v in poly) p.Add(ToPoint64(v.Easting, v.Northing));
        return p;
    }

    private static Point64 ToPoint64(double x, double y)
        => new((long)Math.Round(x * Scale), (long)Math.Round(y * Scale));

    private static List<Vec2> FromPath64(Path64 path)
    {
        var v = new List<Vec2>(path.Count);
        foreach (var p in path) v.Add(new Vec2(p.X / Scale, p.Y / Scale));
        return v;
    }

    private static List<Vec2> EnsureCcw(List<Vec2> poly) => PolygonOrientation.Ensure(poly, wantCcw: true);
    private static List<Vec2> EnsureCw(List<Vec2> poly) => PolygonOrientation.Ensure(poly, wantCcw: false);
    private static List<List<Vec2>> EnsureAllCw(List<List<Vec2>> polys)
    {
        var r = new List<List<Vec2>>(polys.Count);
        foreach (var p in polys) r.Add(EnsureCw(p));
        return r;
    }
}
