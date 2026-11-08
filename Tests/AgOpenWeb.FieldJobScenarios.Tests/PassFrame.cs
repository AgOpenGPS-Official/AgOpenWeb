// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The passes' own coordinates, from the reference line the operator made: <see cref="U"/>
/// across the passes (+ = right of the reference's direction, as AgOpenWeb counts passes) and
/// <see cref="V"/> along them (the arc length of the nearest point of the reference). Pass k
/// runs at u = (k + ½) × spacing. A straight AB line, a curve (open or a closed loop such as the
/// boundary curve) and a water pivot's circle each have one.
/// </summary>
public abstract class PassFrame
{
    public abstract double U((double E, double N) p);
    public abstract double V((double E, double N) p);
    /// <summary>Unit direction of the reference (increasing v) at the point nearest <paramref name="p"/>.</summary>
    public abstract (double E, double N) Tangent((double E, double N) p);
    /// <summary>The point at (u, v); v past the ends of an open reference runs on along its end.</summary>
    public abstract (double E, double N) At(double u, double v);
    /// <summary>Length of the reference along v: a closed loop's perimeter, an open line's length.</summary>
    public abstract double Length { get; }
    public abstract bool Closed { get; }

    /// <summary>Right-hand normal of the reference at the point nearest <paramref name="p"/>: the way u grows.</summary>
    public (double E, double N) AcrossDir((double E, double N) p)
    {
        var t = Tangent(p);
        return (t.N, -t.E);
    }

    /// <summary>The heading (radians, clockwise from north) of the reference at the point nearest <paramref name="p"/>.</summary>
    public double TangentHeading((double E, double N) p)
    {
        var t = Tangent(p);
        return Math.Atan2(t.E, t.N);
    }

    /// <summary>Signed v from <paramref name="from"/> to <paramref name="to"/>: on a closed loop the
    /// shorter way round, so a lap adds up to <see cref="Length"/>.</summary>
    public double DeltaV(double from, double to)
    {
        double d = to - from;
        if (!Closed || Length <= 0) return d;
        d = Math.IEEERemainder(d, Length);
        return d;
    }
}

/// <summary>A straight AB line: u and v in the A→B frame (the harness's original frame).</summary>
public sealed class StraightFrame : PassFrame
{
    private readonly (double E, double N) _a, _along, _across;
    public StraightFrame((double E, double N) a, (double E, double N) along)
    {
        _a = a; _along = along; _across = (along.N, -along.E);
    }
    public override double U((double E, double N) p) => (p.E - _a.E) * _across.E + (p.N - _a.N) * _across.N;
    public override double V((double E, double N) p) => (p.E - _a.E) * _along.E + (p.N - _a.N) * _along.N;
    public override (double E, double N) Tangent((double E, double N) p) => _along;
    public override (double E, double N) At(double u, double v) =>
        (_a.E + _across.E * u + _along.E * v, _a.N + _across.N * u + _along.N * v);
    public override double Length => 0;
    public override bool Closed => false;
}

/// <summary>A curve (a polyline, open or closed): u is the signed distance to its nearest
/// point, v the arc length there. Segments are found through a 10 m bucket grid.</summary>
public sealed class PolylineFrame : PassFrame
{
    private readonly List<(double E, double N)> _p;
    private readonly double[] _s;          // arc length at each vertex
    private readonly bool _closed;
    private readonly int _segs;
    private readonly Dictionary<(int, int), List<int>> _buckets = new();
    private const double Bucket = 10;

    public PolylineFrame(IReadOnlyList<(double E, double N)> points, bool closed)
    {
        _p = new List<(double E, double N)>();
        foreach (var q in points)
            if (_p.Count == 0 || Dist(_p[^1], q) > 0.01) _p.Add(q);
        if (closed && _p.Count > 2 && Dist(_p[0], _p[^1]) < 0.01) _p.RemoveAt(_p.Count - 1);
        _closed = closed;
        _segs = closed ? _p.Count : _p.Count - 1;
        _s = new double[_p.Count + 1];
        for (int i = 0; i < _segs; i++) _s[i + 1] = _s[i] + Dist(_p[i], _p[(i + 1) % _p.Count]);
        for (int i = 0; i < _segs; i++)
        {
            var a = _p[i]; var b = _p[(i + 1) % _p.Count];
            int x0 = (int)Math.Floor(Math.Min(a.E, b.E) / Bucket), x1 = (int)Math.Floor(Math.Max(a.E, b.E) / Bucket);
            int y0 = (int)Math.Floor(Math.Min(a.N, b.N) / Bucket), y1 = (int)Math.Floor(Math.Max(a.N, b.N) / Bucket);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (!_buckets.TryGetValue((x, y), out var l)) _buckets[(x, y)] = l = new List<int>();
                    l.Add(i);
                }
        }
    }

    public IReadOnlyList<(double E, double N)> Points => _p;
    public override double Length => _s[_segs];
    public override bool Closed => _closed;

    private static double Dist((double E, double N) a, (double E, double N) b) =>
        Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));

    /// <summary>The nearest segment and where on it (0..1, clamped except past an open line's ends).</summary>
    private (int Seg, double T, double D2) Nearest((double E, double N) q)
    {
        int bx = (int)Math.Floor(q.E / Bucket), by = (int)Math.Floor(q.N / Bucket);
        int bestSeg = -1; double bestT = 0, bestD2 = double.MaxValue;
        for (int ring = 0; ring < 400; ring++)
        {
            bool any = false;
            for (int x = bx - ring; x <= bx + ring; x++)
                for (int y = by - ring; y <= by + ring; y++)
                {
                    if (Math.Max(Math.Abs(x - bx), Math.Abs(y - by)) != ring) continue;
                    if (!_buckets.TryGetValue((x, y), out var l)) continue;
                    any = true;
                    foreach (int i in l)
                    {
                        var a = _p[i]; var b = _p[(i + 1) % _p.Count];
                        double dx = b.E - a.E, dy = b.N - a.N, l2 = dx * dx + dy * dy;
                        double t = l2 < 1e-12 ? 0 : Math.Clamp(((q.E - a.E) * dx + (q.N - a.N) * dy) / l2, 0, 1);
                        double px = a.E + t * dx - q.E, py = a.N + t * dy - q.N, d2 = px * px + py * py;
                        if (d2 < bestD2) { bestD2 = d2; bestSeg = i; bestT = t; }
                    }
                }
            // Anything in a further ring is at least (ring) buckets away.
            if (bestSeg >= 0 && (ring * Bucket) * (ring * Bucket) > bestD2) break;
            if (!any && ring > 0 && bestSeg >= 0 && (ring * Bucket) * (ring * Bucket) > bestD2) break;
        }
        if (bestSeg < 0) bestSeg = 0;
        if (!_closed)
        {
            // Past the ends of an open line, u and v run on along the end segment.
            var a = _p[bestSeg]; var b = _p[bestSeg + 1];
            double dx = b.E - a.E, dy = b.N - a.N, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : ((q.E - a.E) * dx + (q.N - a.N) * dy) / l2;
            if (bestSeg == 0 && t < 0) bestT = t;
            else if (bestSeg == _segs - 1 && t > 1) bestT = t;
        }
        return (bestSeg, bestT, bestD2);
    }

    private (double E, double N) SegDir(int i)
    {
        var a = _p[i]; var b = _p[(i + 1) % _p.Count];
        double l = Dist(a, b);
        return l < 1e-9 ? (0, 1) : ((b.E - a.E) / l, (b.N - a.N) / l);
    }

    /// <summary>The signed distance to the nearest point of the line (+ to the right of its
    /// direction). Where that point is a vertex, on the outer side of a bend, it is the distance to
    /// the vertex, not along one segment's normal, which comes out short there. Past an open line's
    /// ends it is measured along the end segment's normal, as the line runs on.</summary>
    public override double U((double E, double N) p)
    {
        var (i, t, d2) = Nearest(p);
        var a = _p[i]; var d = SegDir(i);
        double segLen = _s[i + 1] - _s[i];
        double fe = a.E + d.E * t * segLen, fn = a.N + d.N * t * segLen;
        // + to the right of the direction of travel along the line
        double perp = (p.E - fe) * d.N - (p.N - fn) * d.E;
        if (t < 0 || t > 1) return perp;
        return Math.Sign(perp) * Math.Sqrt(d2);
    }

    public override double V((double E, double N) p)
    {
        var (i, t, _) = Nearest(p);
        return _s[i] + t * (_s[i + 1] - _s[i]);
    }

    public override (double E, double N) Tangent((double E, double N) p) => SegDir(Nearest(p).Seg);

    public override (double E, double N) At(double u, double v)
    {
        if (_closed) { v %= Length; if (v < 0) v += Length; }
        int i;
        if (v <= 0) i = 0;
        else if (v >= Length) i = _segs - 1;
        else { i = Array.BinarySearch(_s, 0, _segs + 1, v); if (i < 0) i = ~i - 1; i = Math.Clamp(i, 0, _segs - 1); }
        var a = _p[i]; var d = SegDir(i);
        double along = v - _s[i];
        return (a.E + d.E * along + d.N * u, a.N + d.N * along - d.E * u);
    }
}

/// <summary>A water pivot's circle, driven clockwise as AgOpenWeb builds it: u grows towards
/// the centre (to the right of a clockwise lap), v is the arc length on the reference circle
/// from north.</summary>
public sealed class CircleFrame : PassFrame
{
    public readonly (double E, double N) Centre;
    public readonly double Radius;
    public CircleFrame((double E, double N) centre, double radius) { Centre = centre; Radius = radius; }
    private double R((double E, double N) p) => Math.Sqrt((p.E - Centre.E) * (p.E - Centre.E) + (p.N - Centre.N) * (p.N - Centre.N));
    private double Angle((double E, double N) p)
    {
        double a = Math.Atan2(p.E - Centre.E, p.N - Centre.N);   // clockwise from north
        return a < 0 ? a + 2 * Math.PI : a;
    }
    public override double U((double E, double N) p) => Radius - R(p);
    public override double V((double E, double N) p) => Angle(p) * Radius;
    public override (double E, double N) Tangent((double E, double N) p)
    {
        double a = Angle(p);
        return (Math.Cos(a), -Math.Sin(a));   // clockwise
    }
    public override (double E, double N) At(double u, double v)
    {
        double a = v / Radius, r = Radius - u;
        return (Centre.E + r * Math.Sin(a), Centre.N + r * Math.Cos(a));
    }
    public override double Length => 2 * Math.PI * Radius;
    public override bool Closed => true;
}
