// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Classifies polygon vertices as BCD critical points relative to a sweep
/// direction. A vertex is critical when both adjacent edges go in the same
/// direction (both forward or both backward) along the sweep.
///
/// Type assignment depends on which polygon role the vertex belongs to:
/// <list type="bullet">
/// <item>Outer (CCW): LEFT vertex at the polygon's global minimum is OPEN; any
/// other LEFT vertex is SPLIT (concave outer / notch). RIGHT at global max is
/// CLOSE; other RIGHT is MERGE.</item>
/// <item>Inner hole (CW): every LEFT vertex is SPLIT (sweep starts to skirt
/// the hole here); every RIGHT vertex is MERGE.</item>
/// </list>
///
/// Degenerate cases (polygon edge parallel to sweep) are handled by perturbing
/// the sweep heading by a small epsilon — see <see cref="AdjustSweepForNonDegenerateOrder"/>.
/// </summary>
internal static class CriticalPointClassifier
{
    private const double DegenerateEpsilon = 1e-6;
    private const double DegeneratePerturbation = 1e-4; // ~0.006 degrees

    /// <summary>
    /// If any polygon edge is nearly parallel to the sweep direction, return
    /// a slightly rotated heading so vertices have unique sweep coordinates.
    /// </summary>
    public static double AdjustSweepForNonDegenerateOrder(
        double sweepHeading, List<Vec2> outer, List<List<Vec2>> inners)
    {
        if (HasParallelEdge(sweepHeading, outer)) return sweepHeading + DegeneratePerturbation;
        foreach (var inner in inners)
        {
            if (HasParallelEdge(sweepHeading, inner))
                return sweepHeading + DegeneratePerturbation;
        }
        return sweepHeading;
    }

    private static bool HasParallelEdge(double sweepHeading, List<Vec2> polygon)
    {
        if (polygon.Count < 2) return false;
        double sx = Math.Sin(sweepHeading);
        double sy = Math.Cos(sweepHeading);
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            double dx = b.Easting - a.Easting;
            double dy = b.Northing - a.Northing;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < DegenerateEpsilon) continue;
            // Degenerate when both endpoints have the same sweep coordinate —
            // i.e., the edge is perpendicular to sweep direction. Dot product:
            //   sweep(b) - sweep(a) = dx*sx + dy*sy
            // ≈ 0 means same sweep coord on both ends.
            double dot = dx * sx + dy * sy;
            if (Math.Abs(dot) / len < DegenerateEpsilon) return true;
        }
        return false;
    }

    /// <summary>
    /// Project a point onto the sweep direction (unit vector at heading angle).
    /// Heading 0 = north (sin=0, cos=1), so sweep coord = N. Heading π/2 = east,
    /// sweep coord = E. The convention matches the rest of the codebase.
    /// </summary>
    public static double SweepCoord(Vec2 p, double sweepHeading)
    {
        double sx = Math.Sin(sweepHeading);
        double sy = Math.Cos(sweepHeading);
        return p.Easting * sx + p.Northing * sy;
    }

    /// <summary>
    /// Classify every vertex of a polygon. The polygon must already have the
    /// correct orientation: CCW for outer, CW for inner hole (use
    /// <see cref="PolygonOrientation.Ensure"/>).
    /// </summary>
    public static List<CriticalPoint> Classify(
        List<Vec2> polygon, double sweepHeading, bool isInnerHole)
    {
        var result = new List<CriticalPoint>(polygon.Count);
        if (polygon.Count < 3) return result;

        // Per-vertex sweep coords up front (used multiple times).
        var s = new double[polygon.Count];
        double minS = double.PositiveInfinity, maxS = double.NegativeInfinity;
        for (int i = 0; i < polygon.Count; i++)
        {
            s[i] = SweepCoord(polygon[i], sweepHeading);
            if (s[i] < minS) minS = s[i];
            if (s[i] > maxS) maxS = s[i];
        }

        for (int i = 0; i < polygon.Count; i++)
        {
            int prev = (i - 1 + polygon.Count) % polygon.Count;
            int next = (i + 1) % polygon.Count;

            // Direction of each adjacent edge from V's perspective:
            //   "forward" if the other endpoint is at higher sweep coord.
            bool prevForward = s[prev] > s[i];
            bool nextForward = s[next] > s[i];

            CriticalPointType type;
            if (prevForward && nextForward)
            {
                // LEFT vertex (both adjacent above V).
                if (isInnerHole)
                    type = CriticalPointType.Split;
                else
                    type = (Math.Abs(s[i] - minS) < DegenerateEpsilon)
                        ? CriticalPointType.Open
                        : CriticalPointType.Split;
            }
            else if (!prevForward && !nextForward)
            {
                // RIGHT vertex (both adjacent below V).
                if (isInnerHole)
                    type = CriticalPointType.Merge;
                else
                    type = (Math.Abs(s[i] - maxS) < DegenerateEpsilon)
                        ? CriticalPointType.Close
                        : CriticalPointType.Merge;
            }
            else
            {
                type = CriticalPointType.Regular;
            }

            result.Add(new CriticalPoint
            {
                Position = polygon[i],
                Type = type,
                SweepCoordinate = s[i],
                VertexIndex = i,
                IsOnInnerBoundary = isInnerHole,
            });
        }

        return result;
    }
}
