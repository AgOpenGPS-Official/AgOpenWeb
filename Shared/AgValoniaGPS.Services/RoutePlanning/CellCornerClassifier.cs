// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Tags each cell's four bounding-box corners (in the sweep-aligned frame)
/// as <see cref="CellCornerKind.OuterHeadland"/> (vertex on the outer
/// clip-boundary), <see cref="CellCornerKind.InnerHeadland"/> (vertex on an
/// expanded inner-ring boundary — the obstacle's no-work buffer), or
/// <see cref="CellCornerKind.Internal"/> (vertex on a decomposition cut, no
/// headland nearby). The route stitcher uses this to decide which corners
/// are valid for cell entry/exit and for U-turn endpoints.
/// </summary>
public static class CellCornerClassifier
{
    /// <summary>
    /// Tolerance for the "vertex lies on a boundary edge" test, in meters.
    /// Generous enough to absorb the 1cm cut-end extension and Clipper2's
    /// 0.1mm coordinate quantization.
    /// </summary>
    private const double OnBoundaryTolerance = 0.05;

    /// <summary>
    /// Classify cell corners against the outer clip-boundary only — back-compat
    /// overload for cell sets with no inner rings.
    /// </summary>
    public static void ClassifyAll(IEnumerable<Cell> cells, List<Vec2> outerBoundary, double sweepHeading)
        => ClassifyAll(cells, outerBoundary, null, sweepHeading);

    /// <summary>
    /// Classify cell corners against the outer clip-boundary AND each
    /// expanded inner-ring boundary. <paramref name="expandedInnerRings"/>
    /// must be the SAME rings the cells were decomposed against (i.e. already
    /// outward-offset by HeadlandDistance), so cell vertices that sit on an
    /// inner-ring edge get tagged <see cref="CellCornerKind.InnerHeadland"/>.
    /// </summary>
    public static void ClassifyAll(
        IEnumerable<Cell> cells,
        List<Vec2> outerBoundary,
        IReadOnlyList<List<Vec2>>? expandedInnerRings,
        double sweepHeading)
    {
        if (outerBoundary == null || outerBoundary.Count < 3) return;
        double sx = Math.Sin(sweepHeading);
        double sy = Math.Cos(sweepHeading);
        foreach (var cell in cells)
            ClassifyOne(cell, outerBoundary, expandedInnerRings, sx, sy);
    }

    private static void ClassifyOne(
        Cell cell,
        List<Vec2> outerBoundary,
        IReadOnlyList<List<Vec2>>? expandedInnerRings,
        double sx, double sy)
    {
        if (cell.Polygon == null || cell.Polygon.Count == 0) return;

        // Project to (sweep, perp) frame and find bounding box.
        double sMin = double.PositiveInfinity, sMax = double.NegativeInfinity;
        double pMin = double.PositiveInfinity, pMax = double.NegativeInfinity;
        var rotated = new List<Vec2>(cell.Polygon.Count);
        foreach (var v in cell.Polygon)
        {
            double s = v.Easting * sx + v.Northing * sy;
            double p = -v.Easting * sy + v.Northing * sx;
            rotated.Add(new Vec2(s, p));
            if (s < sMin) sMin = s;
            if (s > sMax) sMax = s;
            if (p < pMin) pMin = p;
            if (p > pMax) pMax = p;
        }

        // For each bounding-box corner, find the cell vertex closest to it
        // and classify by which boundary (if any) it lies on. Outer takes
        // precedence over inner — if a vertex sits at the corner where the
        // outer headland meets an expanded inner ring, treat it as outer.
        var corners = new[]
        {
            (CellCorner.LowSweepLowPerp,   new Vec2(sMin, pMin)),
            (CellCorner.LowSweepHighPerp,  new Vec2(sMin, pMax)),
            (CellCorner.HighSweepLowPerp,  new Vec2(sMax, pMin)),
            (CellCorner.HighSweepHighPerp, new Vec2(sMax, pMax)),
        };

        foreach (var (corner, target) in corners)
        {
            int idx = ClosestVertex(rotated, target);
            var origVertex = cell.Polygon[idx];
            CellCornerKind kind;
            if (IsOnBoundary(origVertex, outerBoundary))
                kind = CellCornerKind.OuterHeadland;
            else if (expandedInnerRings != null && IsOnAnyRing(origVertex, expandedInnerRings))
                kind = CellCornerKind.InnerHeadland;
            else
                kind = CellCornerKind.Internal;
            cell.SetCornerKind(corner, kind);
        }
    }

    private static int ClosestVertex(List<Vec2> rotated, Vec2 target)
    {
        int best = 0;
        double bestD2 = double.PositiveInfinity;
        for (int i = 0; i < rotated.Count; i++)
        {
            double dx = rotated[i].Easting - target.Easting;
            double dy = rotated[i].Northing - target.Northing;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = i; }
        }
        return best;
    }

    private static bool IsOnBoundary(Vec2 point, List<Vec2> ring)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            if (GeometryMath.PointToSegmentDistance(point, a, b) <= OnBoundaryTolerance)
                return true;
        }
        return false;
    }

    private static bool IsOnAnyRing(Vec2 point, IReadOnlyList<List<Vec2>> rings)
    {
        foreach (var r in rings)
            if (r != null && r.Count >= 3 && IsOnBoundary(point, r)) return true;
        return false;
    }
}
