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
/// as HEADLAND or INTERNAL based on whether the cell vertex closest to that
/// corner lies on the original outer field boundary. Used by the route
/// planner to constrain cell entry/exit options to headland-reachable
/// corners only.
/// </summary>
public static class CellCornerClassifier
{
    /// <summary>
    /// Tolerance for the "vertex lies on outer boundary edge" test, in meters.
    /// Generous enough to absorb the 1cm cut-end extension and Clipper2's
    /// 0.1mm coordinate quantization.
    /// </summary>
    private const double OnBoundaryTolerance = 0.05;

    public static void ClassifyAll(IEnumerable<Cell> cells, List<Vec2> outerBoundary, double sweepHeading)
    {
        if (outerBoundary == null || outerBoundary.Count < 3) return;
        double sx = Math.Sin(sweepHeading);
        double sy = Math.Cos(sweepHeading);
        foreach (var cell in cells)
            ClassifyOne(cell, outerBoundary, sx, sy);
    }

    private static void ClassifyOne(Cell cell, List<Vec2> outerBoundary, double sx, double sy)
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
        // and classify by whether that vertex lies on the outer boundary.
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
            cell.SetCornerKind(corner,
                IsOnOuterBoundary(origVertex, outerBoundary)
                    ? CellCornerKind.Headland
                    : CellCornerKind.Internal);
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

    private static bool IsOnOuterBoundary(Vec2 point, List<Vec2> outer)
    {
        int n = outer.Count;
        for (int i = 0; i < n; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % n];
            if (GeometryMath.PointToSegmentDistance(point, a, b) <= OnBoundaryTolerance)
                return true;
        }
        return false;
    }
}
