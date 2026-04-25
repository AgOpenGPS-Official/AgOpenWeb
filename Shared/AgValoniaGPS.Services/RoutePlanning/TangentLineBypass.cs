// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.
//
// =============================================================================
// Bypass-path geometry for swaths split by an undrivable inner ring.
//
// Given two split-sibling endpoints A and B on opposite sides of an obstacle,
// build the shortest polyline that runs from A, follows tangents to the
// obstacle's convex hull, walks along hull edges, and exits to B. Used by
// the route stitcher when reconnecting segments of a swath that the
// rotational generator broke at an undrivable hole.
//
// For drivable obstacles, callers should use <see cref="StraightConnector"/>
// instead — the swath stays a single piece and section control engages over
// the obstacle.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Services.RoutePlanning;

public static class TangentLineBypass
{
    /// <summary>
    /// Straight A→B polyline — the connector for drivable obstacles, where
    /// section control engages while the implement passes over the obstacle.
    /// </summary>
    public static List<Vec2> StraightConnector(Vec2 a, Vec2 b) => new() { a, b };

    /// <summary>
    /// Shortest bypass from A to B around a convex obstacle. The result
    /// includes A and B; intermediate points are tangent points and hull
    /// edge vertices. If <paramref name="convexObstacle"/> is fewer than
    /// 3 points, returns the straight line.
    /// </summary>
    /// <param name="convexObstacle">Convex hull of the obstacle (any orientation).</param>
    public static List<Vec2> Bypass(Vec2 a, Vec2 b, IReadOnlyList<Vec2> convexObstacle)
    {
        if (convexObstacle == null || convexObstacle.Count < 3)
            return StraightConnector(a, b);

        // Normalize the hull to CCW so walk-direction reasoning is consistent.
        var hull = EnsureCcw(convexObstacle);

        var tA = FindTangentIndices(a, hull);
        var tB = FindTangentIndices(b, hull);
        if (tA.Count < 2 || tB.Count < 2)
            return StraightConnector(a, b);

        // Try all (a-tangent, b-tangent, walking direction) combinations and
        // pick the shortest. For a convex hull, all 8 paths are valid (none
        // crosses the hull); the optimum is just the geometric minimum.
        List<Vec2>? best = null;
        double bestLen = double.PositiveInfinity;
        foreach (int ai in tA)
        {
            foreach (int bi in tB)
            {
                for (int dir = 0; dir < 2; dir++)
                {
                    var path = BuildPath(a, b, hull, ai, bi, ccw: dir == 0);
                    double len = PolylineLength(path);
                    if (len < bestLen) { bestLen = len; best = path; }
                }
            }
        }
        return best ?? StraightConnector(a, b);
    }

    /// <summary>
    /// Andrew's monotone chain — convex hull of an arbitrary point set,
    /// returned in CCW order. Use this on a non-convex obstacle ring before
    /// passing to <see cref="Bypass"/>.
    /// </summary>
    public static List<Vec2> ConvexHull(IReadOnlyList<Vec2> points)
    {
        int n = points.Count;
        if (n <= 1) return new List<Vec2>(points);

        var sorted = points
            .OrderBy(p => p.Easting)
            .ThenBy(p => p.Northing)
            .ToList();

        // Lower hull.
        var lower = new List<Vec2>();
        foreach (var p in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        // Upper hull.
        var upper = new List<Vec2>();
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            var p = sorted[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        // Concatenate (drop the duplicated start/end of each chain).
        if (lower.Count > 0) lower.RemoveAt(lower.Count - 1);
        if (upper.Count > 0) upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    // -- Internals --

    /// <summary>
    /// A vertex V of a convex polygon is a tangent point from external P iff
    /// both adjacent vertices lie on the same side of line P–V. Returns the
    /// (typically 2) tangent indices.
    /// </summary>
    private static List<int> FindTangentIndices(Vec2 p, IReadOnlyList<Vec2> hull)
    {
        int n = hull.Count;
        var result = new List<int>(2);
        for (int i = 0; i < n; i++)
        {
            var prev = hull[(i - 1 + n) % n];
            var next = hull[(i + 1) % n];
            double cprev = Cross(p, hull[i], prev);
            double cnext = Cross(p, hull[i], next);
            // Same sign on both sides (with small tolerance for collinear cases).
            if ((cprev > 1e-9 && cnext > 1e-9) || (cprev < -1e-9 && cnext < -1e-9))
                result.Add(i);
        }
        return result;
    }

    private static List<Vec2> BuildPath(
        Vec2 a, Vec2 b, IReadOnlyList<Vec2> hull, int aIdx, int bIdx, bool ccw)
    {
        var path = new List<Vec2> { a };
        int n = hull.Count;
        int i = aIdx;
        path.Add(hull[i]);
        // Walk hull from aIdx to bIdx in chosen direction. Bound the loop at n+1
        // so a malformed hull can't spin forever.
        int safety = 0;
        while (i != bIdx && safety++ <= n)
        {
            i = ccw ? (i + 1) % n : (i - 1 + n) % n;
            path.Add(hull[i]);
        }
        path.Add(b);
        return path;
    }

    private static double PolylineLength(IReadOnlyList<Vec2> path)
    {
        double sum = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double de = path[i].Easting - path[i - 1].Easting;
            double dn = path[i].Northing - path[i - 1].Northing;
            sum += Math.Sqrt(de * de + dn * dn);
        }
        return sum;
    }

    /// <summary>(b - a) × (c - a) — positive if a, b, c are CCW.</summary>
    private static double Cross(Vec2 a, Vec2 b, Vec2 c) =>
        (b.Easting - a.Easting) * (c.Northing - a.Northing) -
        (b.Northing - a.Northing) * (c.Easting - a.Easting);

    private static List<Vec2> EnsureCcw(IReadOnlyList<Vec2> poly)
    {
        if (SignedArea(poly) >= 0) return new List<Vec2>(poly);
        var rev = new List<Vec2>(poly.Count);
        for (int i = poly.Count - 1; i >= 0; i--) rev.Add(poly[i]);
        return rev;
    }

    private static double SignedArea(IReadOnlyList<Vec2> poly)
    {
        double a = 0;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % n];
            a += (q.Easting - p.Easting) * (q.Northing + p.Northing);
        }
        // Negative sum-form ↔ positive (CCW) area.
        return -a / 2.0;
    }
}
