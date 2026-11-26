// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Guidance;

/// <summary>
/// The way along a fence (a closed boundary ring) between two points on it, as AgOpenGPS's
/// FormABDraw makes a curve from two picks on the fence: the fence's own points between them,
/// the shorter way round.
/// </summary>
public static class FencePath
{
    /// <summary>The side (ring point i to i + 1) nearest a point, how far along it, the point on it and the distance.</summary>
    public static (int Side, double T, Vec2 Point, double Distance) Project(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        int n = ring.Count;
        (int, double, Vec2, double) best = (0, 0, ring[0], double.MaxValue);
        for (int i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? Math.Clamp(((p.Easting - a.Easting) * dx + (p.Northing - a.Northing) * dy) / l2, 0, 1) : 0;
            var q = new Vec2(a.Easting + t * dx, a.Northing + t * dy);
            double d = Math.Sqrt((q.Easting - p.Easting) * (q.Easting - p.Easting) + (q.Northing - p.Northing) * (q.Northing - p.Northing));
            if (d < best.Item4) best = (i, t, q, d);
        }
        return best;
    }

    /// <summary>
    /// The fence from <paramref name="from"/> to <paramref name="to"/> (both projected onto it), the
    /// shorter way round, with a point at least every <paramref name="spacing"/> metres. The first
    /// and last points are the two projections.
    /// </summary>
    public static List<Vec3> ShorterRun(IReadOnlyList<Vec2> ring, Vec2 from, Vec2 to, double spacing = 1.0)
    {
        var a = Project(ring, from);
        var b = Project(ring, to);
        var fwd = Walk(ring, a, b, 1);
        var back = Walk(ring, a, b, -1);
        var run = Length(fwd) <= Length(back) ? fwd : back;
        var pts = new List<Vec3>(run.Count);
        foreach (var p in run) pts.Add(new Vec3(p.Easting, p.Northing, 0));
        return CurveProcessing.Densify(pts, spacing);
    }

    private static List<Vec2> Walk(IReadOnlyList<Vec2> ring, (int Side, double T, Vec2 Point, double D) a,
        (int Side, double T, Vec2 Point, double D) b, int dir)
    {
        int n = ring.Count;
        var path = new List<Vec2> { a.Point };
        bool sameSide = a.Side == b.Side && (dir > 0 ? a.T <= b.T : b.T <= a.T);
        if (!sameSide)
        {
            int k = dir > 0 ? (a.Side + 1) % n : a.Side;
            int last = dir > 0 ? b.Side : (b.Side + 1) % n;
            while (true)
            {
                path.Add(ring[k]);
                if (k == last) break;
                k = (k + dir + n) % n;
            }
        }
        path.Add(b.Point);
        return path;
    }

    private static double Length(List<Vec2> path)
    {
        double len = 0;
        for (int i = 1; i < path.Count; i++)
            len += Math.Sqrt(Math.Pow(path[i].Easting - path[i - 1].Easting, 2) + Math.Pow(path[i].Northing - path[i - 1].Northing, 2));
        return len;
    }
}
