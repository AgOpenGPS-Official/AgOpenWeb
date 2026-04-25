// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Ray-vs-polygon-edges intersection used by the F2C-style decomposition
/// (TrapezoidalDecomp / BoustrophedonDecomp). For each critical vertex the
/// decomposition casts a ray perpendicular to the sweep direction and finds
/// where it first crosses any boundary edge — that crossing point is the
/// other endpoint of a cell-splitting cut.
/// </summary>
internal static class Raycast
{
    private const double Eps = 1e-9;

    /// <summary>
    /// Find the closest intersection of the ray (origin + t·direction, t &gt; 0)
    /// with any edge of any polygon in <paramref name="polygons"/>, with edges
    /// incident on a vertex coincident with <paramref name="origin"/> excluded.
    /// Returns null when the ray hits nothing.
    /// </summary>
    public static Vec2? FirstHit(Vec2 origin, Vec2 direction, IEnumerable<List<Vec2>> polygons,
        double minT = Eps)
    {
        double bestT = double.PositiveInfinity;
        Vec2? hit = null;

        foreach (var poly in polygons)
        {
            int n = poly.Count;
            if (n < 2) continue;
            for (int i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];

                // Skip the two edges incident on origin (zero-length cut).
                if (CoincidentVertex(a, origin) || CoincidentVertex(b, origin))
                    continue;

                if (RaySegmentIntersect(origin, direction, a, b, out double t) && t > minT && t < bestT)
                {
                    bestT = t;
                    hit = new Vec2(origin.Easting + t * direction.Easting, origin.Northing + t * direction.Northing);
                }
            }
        }

        return hit;
    }

    private static bool CoincidentVertex(Vec2 a, Vec2 b) =>
        Math.Abs(a.Easting - b.Easting) < Eps && Math.Abs(a.Northing - b.Northing) < Eps;

    /// <summary>
    /// Solve  origin + t·direction = a + u·(b-a)  with  t &gt;= 0  and  0 &lt;= u &lt;= 1.
    /// Returns true and sets t when the ray hits the segment in front of the origin.
    /// </summary>
    private static bool RaySegmentIntersect(Vec2 origin, Vec2 direction, Vec2 a, Vec2 b, out double t)
    {
        t = 0;
        double sx = b.Easting - a.Easting;
        double sy = b.Northing - a.Northing;
        // Cross of direction with segment direction; near-zero means parallel.
        double denom = direction.Easting * sy - direction.Northing * sx;
        if (Math.Abs(denom) < Eps) return false;

        double rx = a.Easting - origin.Easting;
        double ry = a.Northing - origin.Northing;
        double tt = (rx * sy - ry * sx) / denom;
        double uu = (rx * direction.Northing - ry * direction.Easting) / denom;
        if (tt < 0) return false;
        if (uu < -Eps || uu > 1 + Eps) return false;

        t = tt;
        return true;
    }
}
