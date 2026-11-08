// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

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

    public static double Perimeter(IReadOnlyList<(double E, double N)> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            s += Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
        }
        return s;
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

/// <summary>
/// The truth coverage: a grid of 0.2 m cells over the field. Each cell counts how many times a
/// section of the real implement passed over it with its valve open (the module's relay, not
/// the screen), and knows where it lies: inside the outer boundary, inside the headland line
/// (the cultivated area), inside an obstacle.
/// </summary>
public sealed class TruthGrid
{
    public const double Cell = 0.2;
    public const byte InOuter = 1, InWork = 2, InObstacle = 4, InDriveThrough = 8;

    public readonly double MinE, MinN;
    public readonly int W, H;
    public readonly byte[] Count;
    public readonly byte[] Zone;

    public TruthGrid(double minE, double maxE, double minN, double maxN, double margin = 40)
    {
        MinE = minE - margin; MinN = minN - margin;
        W = (int)Math.Ceiling((maxE - minE + 2 * margin) / Cell);
        H = (int)Math.Ceiling((maxN - minN + 2 * margin) / Cell);
        Count = new byte[W * H];
        Zone = new byte[W * H];
    }

    public (double E, double N) Centre(int i) => (MinE + (i % W + 0.5) * Cell, MinN + (i / W + 0.5) * Cell);

    public int Index(double e, double n)
    {
        int x = (int)Math.Floor((e - MinE) / Cell), y = (int)Math.Floor((n - MinN) / Cell);
        return x < 0 || y < 0 || x >= W || y >= H ? -1 : y * W + x;
    }

    public void Classify(IReadOnlyList<(double E, double N)> outer, IReadOnlyList<(double E, double N)>? work,
        IEnumerable<(bool DriveThrough, IReadOnlyList<(double E, double N)> Ring)> inner)
    {
        var inn = inner.ToList();
        for (int i = 0; i < Zone.Length; i++)
        {
            var c = Centre(i);
            byte z = 0;
            if (Ring.Inside(c, outer))
            {
                z |= InOuter;
                if (work == null || Ring.Inside(c, work)) z |= InWork;
                foreach (var (dt, ring) in inn)
                    if (Ring.Inside(c, ring)) z |= dt ? InDriveThrough : InObstacle;
            }
            Zone[i] = z;
        }
    }

    /// <summary>The strip a section swept between two fixes: the quad of its previous and current
    /// left and right ends. Every cell whose centre lies in it counts one more pass.</summary>
    public void Sweep((double E, double N) pl, (double E, double N) pr, (double E, double N) cl, (double E, double N) cr)
    {
        Fill(pl, pr, cr);
        Fill(pl, cr, cl);
    }

    private readonly HashSet<int> _stamp = new();
    private int _stampGen;

    /// <summary>Marks the cells of one fix's sweep once, even where its two triangles overlap.</summary>
    public void BeginFix() { _stamp.Clear(); _stampGen++; }

    private void Fill((double E, double N) a, (double E, double N) b, (double E, double N) c)
    {
        double minE = Math.Min(a.E, Math.Min(b.E, c.E)), maxE = Math.Max(a.E, Math.Max(b.E, c.E));
        double minN = Math.Min(a.N, Math.Min(b.N, c.N)), maxN = Math.Max(a.N, Math.Max(b.N, c.N));
        int x0 = Math.Max(0, (int)Math.Floor((minE - MinE) / Cell)), x1 = Math.Min(W - 1, (int)Math.Floor((maxE - MinE) / Cell));
        int y0 = Math.Max(0, (int)Math.Floor((minN - MinN) / Cell)), y1 = Math.Min(H - 1, (int)Math.Floor((maxN - MinN) / Cell));
        double d = (b.N - c.N) * (a.E - c.E) + (c.E - b.E) * (a.N - c.N);
        if (Math.Abs(d) < 1e-12) return;
        for (int y = y0; y <= y1; y++)
        {
            double n = MinN + (y + 0.5) * Cell;
            for (int x = x0; x <= x1; x++)
            {
                double e = MinE + (x + 0.5) * Cell;
                double l1 = ((b.N - c.N) * (e - c.E) + (c.E - b.E) * (n - c.N)) / d;
                double l2 = ((c.N - a.N) * (e - c.E) + (a.E - c.E) * (n - c.N)) / d;
                double l3 = 1 - l1 - l2;
                if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;
                int i = y * W + x;
                if (!_stamp.Add(i)) continue;
                if (Count[i] < 255) Count[i]++;
            }
        }
    }

    public bool Near(int i, Func<int, bool> test, int cells = 1)
    {
        int x = i % W, y = i / W;
        for (int dy = -cells; dy <= cells; dy++)
            for (int dx = -cells; dx <= cells; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                if (test(yy * W + xx)) return true;
            }
        return false;
    }
}

/// <summary>Areas in m² at the end of a job, from the truth grid and AgOpenWeb's coverage map.</summary>
public sealed class JobAreas
{
    public double Work;                 // inside the headland line (or the boundary), outside obstacles
    public double Sprayed;              // truth, inside the work area, at least once
    public double Missed;               // work area never sprayed
    public double MissedBetweenPasses;  // of which: strips with sprayed ground on both sides across the passes
    public double MissedAtEnds;         // of which: within 3 m of the headland line (the pass ends)
    public double MissedAtObstacles;    // of which: within 2 m of an obstacle (the sections closing and opening over it)
    public double MissedAlongBoundary;  // of which, with no headland: within 1.5 m of the outer boundary
    public double MissedLeftInMiddle;   // of which: the middle no lap reached, left by the operator
    public double MissedAtCorners;      // of which: at a lap's corners
    public double Twice;                // truth ≥ 2 inside the work area
    public double OutsideBoundary;      // truth outside the outer boundary
    public double InObstacles;          // truth inside a hard obstacle
    public double InDriveThrough;       // truth inside a drive-through inner boundary (allowed)
    public double InHeadland;           // truth inside the boundary but outside the headland line
    public double AppPainted;           // AgOpenWeb's map, inside the work area
    public double AppPaintedNotSprayed; // AgOpenWeb painted, no valve open within 0.3 m (anywhere)
    public double AppPaintedScreenSweptNotSprayed; // of which: AgOpenWeb's own sections swept it (on screen), the valves did not
    public double ScreenSwept;          // what AgOpenWeb's sections swept on screen, inside the boundary
    public double ScreenSweptNotPainted; // swept on screen, the map blank within 0.4 m
    public double MapSwept;             // what AgOpenWeb's mapping (IsMappingOn, 100 Hz) swept, inside the boundary
    public double AppPaintedNotMapped;  // the map painted, the mapping sweep blank within 0.4 m (the rasteriser's own doing)
    public double MappedNotScreen;      // the mapping swept, the screen's sections (10 Hz) never there within 0.4 m
    public double SprayedNotAppPainted; // valve open, AgOpenWeb painted nothing within 0.3 m (anywhere)
    public double AppTotalWorked, AppActualCovered;
    public double LargestMissedStripM;  // widest strip between passes, m (from the pass edges)
    public string LargestStripWhere = "";   // which passes and where along the line
    public double MissedBetweenE, MissedBetweenN;   // area-weighted sums, for the centre of the missed strips
    public double LargestOverlapM;      // widest overlap between neighbouring passes, m
}
