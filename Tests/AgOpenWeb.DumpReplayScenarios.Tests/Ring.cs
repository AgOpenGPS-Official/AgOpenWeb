// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>Rings in the field plane (E, N metres).</summary>
public static class Ring
{
    public static bool Inside((double E, double N) q, IReadOnlyList<(double E, double N)> p)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            var (xi, yi) = p[i];
            var (xj, yj) = p[j];
            if ((yi > q.N) != (yj > q.N) && q.E < (xj - xi) * (q.N - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    public static double Area(IReadOnlyList<(double E, double N)> p)
    {
        double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += p[j].E * p[i].N - p[i].E * p[j].N;
        return Math.Abs(a) / 2;
    }

    public static double DistToSegment((double E, double N) q, (double E, double N) a, (double E, double N) b)
    {
        double dx = b.E - a.E, dy = b.N - a.N, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((q.E - a.E) * dx + (q.N - a.N) * dy) / l2, 0, 1);
        double px = a.E + t * dx - q.E, py = a.N + t * dy - q.N;
        return Math.Sqrt(px * px + py * py);
    }

    public static double DistToRing((double E, double N) q, IReadOnlyList<(double E, double N)> ring)
    {
        double best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) best = Math.Min(best, DistToSegment(q, ring[j], ring[i]));
        return best;
    }

    /// <summary>Where the line through <paramref name="a"/> along <paramref name="dir"/> (unit) crosses the
    /// ring, as distances along the line from <paramref name="a"/>, sorted.</summary>
    public static List<double> LineCrossings((double E, double N) a, (double E, double N) dir, IReadOnlyList<(double E, double N)> ring)
    {
        var ts = new List<double>();
        // Right-hand normal of the direction: the signed distance from the line.
        double ne = dir.N, nn = -dir.E;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double si = (ring[i].E - a.E) * ne + (ring[i].N - a.N) * nn;
            double sj = (ring[j].E - a.E) * ne + (ring[j].N - a.N) * nn;
            if ((si > 0) == (sj > 0)) continue;
            double f = si / (si - sj);
            double xe = ring[i].E + (ring[j].E - ring[i].E) * f, xn = ring[i].N + (ring[j].N - ring[i].N) * f;
            ts.Add((xe - a.E) * dir.E + (xn - a.N) * dir.N);
        }
        ts.Sort();
        return ts;
    }
}

