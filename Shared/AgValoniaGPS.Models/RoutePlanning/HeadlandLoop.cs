// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Models.RoutePlanning;

/// <summary>Whether a headland loop wraps the outer field or an inner-ring obstacle.</summary>
public enum HeadlandLoopKind
{
    Outer,
    Inner,
}

/// <summary>
/// A point projected onto a <see cref="HeadlandLoop"/>: nearest position,
/// edge index, parametric position along that edge, total arc length from
/// the loop's start vertex (forward direction), and the unit tangent at the
/// projection (in the loop's positive walk direction).
/// </summary>
public readonly struct LoopProjection
{
    public Vec2 Position { get; }
    public int SegmentIndex { get; }
    public double T { get; }
    public double ArcLength { get; }
    public Vec2 Tangent { get; }

    public LoopProjection(Vec2 position, int segmentIndex, double t, double arcLength, Vec2 tangent)
    {
        Position = position;
        SegmentIndex = segmentIndex;
        T = t;
        ArcLength = arcLength;
        Tangent = tangent;
    }
}

/// <summary>
/// A closed-loop polyline forming one headland track pass. Loops are stored
/// in CCW order regardless of whether they wrap the outer field or an
/// inner-ring obstacle. Provides perimeter projection (find the nearest loop
/// point to an arbitrary 2D point) and perimeter walking (sample the loop
/// between two projections in either direction).
/// </summary>
public sealed class HeadlandLoop
{
    /// <summary>Polygon points in CCW order. Closure is implicit (last → first).</summary>
    public IReadOnlyList<Vec2> Polygon { get; }

    public HeadlandLoopKind Kind { get; }

    /// <summary>0 for the outer-field loop; for inner loops, the index into the original inner-ring list.</summary>
    public int RingIndex { get; }

    /// <summary>0 = outermost pass (closest to the original boundary). Higher = further inward (outer) or further outward (inner).</summary>
    public int PassIndex { get; }

    /// <summary>Total loop perimeter in meters.</summary>
    public double Perimeter { get; }

    /// <summary>Cum[i] = arc length from Polygon[0] to Polygon[i] going forward (CCW). Cum[n] = Perimeter.</summary>
    private readonly double[] _cum;

    public HeadlandLoop(IReadOnlyList<Vec2> polygon, HeadlandLoopKind kind, int ringIndex, int passIndex)
    {
        if (polygon == null || polygon.Count < 3)
            throw new ArgumentException("HeadlandLoop requires at least 3 vertices", nameof(polygon));
        Polygon = polygon;
        Kind = kind;
        RingIndex = ringIndex;
        PassIndex = passIndex;

        int n = polygon.Count;
        _cum = new double[n + 1];
        _cum[0] = 0;
        for (int i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];
            double dx = b.Easting - a.Easting;
            double dy = b.Northing - a.Northing;
            _cum[i + 1] = _cum[i] + Math.Sqrt(dx * dx + dy * dy);
        }
        Perimeter = _cum[n];
    }

    /// <summary>
    /// Find the closest point on the loop to <paramref name="point"/>.
    /// O(n) scan over edges — loops are small (~tens of vertices) so this is fine.
    /// </summary>
    public LoopProjection Project(Vec2 point)
    {
        int n = Polygon.Count;
        int bestIdx = 0;
        double bestT = 0;
        double bestD2 = double.PositiveInfinity;
        Vec2 bestPos = Polygon[0];

        for (int i = 0; i < n; i++)
        {
            var a = Polygon[i];
            var b = Polygon[(i + 1) % n];
            double dx = b.Easting - a.Easting;
            double dy = b.Northing - a.Northing;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : ((point.Easting - a.Easting) * dx + (point.Northing - a.Northing) * dy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double px = a.Easting + t * dx;
            double py = a.Northing + t * dy;
            double ex = point.Easting - px;
            double ey = point.Northing - py;
            double d2 = ex * ex + ey * ey;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestIdx = i;
                bestT = t;
                bestPos = new Vec2(px, py);
            }
        }

        var (tangent, _) = TangentAt(bestIdx);
        double edgeLen = _cum[bestIdx + 1] - _cum[bestIdx];
        double arcLen = _cum[bestIdx] + bestT * edgeLen;
        return new LoopProjection(bestPos, bestIdx, bestT, arcLen, tangent);
    }

    /// <summary>
    /// Forward (CCW) arc length from <paramref name="from"/> to <paramref name="to"/>.
    /// Always in [0, Perimeter).
    /// </summary>
    public double ForwardArc(LoopProjection from, LoopProjection to)
    {
        double d = to.ArcLength - from.ArcLength;
        if (d < 0) d += Perimeter;
        return d;
    }

    /// <summary>
    /// Sample the loop between two projections in the chosen direction.
    /// <paramref name="forward"/> = true walks CCW; false walks CW. Output
    /// is a list of (E, N, heading) poses spaced at most
    /// <paramref name="maxStep"/> apart. First pose ≈ <paramref name="from"/>,
    /// last pose ≈ <paramref name="to"/>; intermediate samples lie exactly on
    /// loop edges (no smoothing — turns at vertices are sharp until the
    /// caller's Dubins splice rounds them off).
    /// </summary>
    public List<Vec3> Walk(LoopProjection from, LoopProjection to, bool forward, double maxStep)
    {
        if (maxStep <= 0) maxStep = 1.0;
        double totalArc = forward ? ForwardArc(from, to) : (Perimeter - ForwardArc(from, to));
        var result = new List<Vec3>();

        if (totalArc < 1e-9)
        {
            // Degenerate: from and to coincide.
            result.Add(SampleAt(from.ArcLength, forward));
            return result;
        }

        // Always emit the start.
        result.Add(SampleAt(from.ArcLength, forward));

        int steps = Math.Max(1, (int)Math.Ceiling(totalArc / maxStep));
        for (int i = 1; i < steps; i++)
        {
            double frac = (double)i / steps;
            double s = from.ArcLength + (forward ? frac * totalArc : -frac * totalArc);
            s = NormalizeArc(s);
            result.Add(SampleAt(s, forward));
        }

        // Snap last waypoint to `to` exactly.
        result.Add(new Vec3(to.Position.Easting, to.Position.Northing, HeadingFromTangent(to.Tangent, forward)));
        return result;
    }

    /// <summary>Pose at a given forward arc-length, with heading aligned to walk direction.</summary>
    private Vec3 SampleAt(double arc, bool forward)
    {
        arc = NormalizeArc(arc);
        // Find edge containing this arc length via linear scan (loops are small).
        int n = Polygon.Count;
        int edge = 0;
        for (int i = 0; i < n; i++)
        {
            if (arc <= _cum[i + 1] + 1e-12) { edge = i; break; }
            if (i == n - 1) edge = n - 1;
        }
        double edgeLen = _cum[edge + 1] - _cum[edge];
        double t = edgeLen < 1e-12 ? 0 : (arc - _cum[edge]) / edgeLen;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        var a = Polygon[edge];
        var b = Polygon[(edge + 1) % n];
        double px = a.Easting + t * (b.Easting - a.Easting);
        double py = a.Northing + t * (b.Northing - a.Northing);
        var (tangent, _) = TangentAt(edge);
        return new Vec3(px, py, HeadingFromTangent(tangent, forward));
    }

    private double NormalizeArc(double s)
    {
        if (Perimeter < 1e-12) return 0;
        s %= Perimeter;
        if (s < 0) s += Perimeter;
        return s;
    }

    private (Vec2 tangent, double length) TangentAt(int edgeIdx)
    {
        var a = Polygon[edgeIdx];
        var b = Polygon[(edgeIdx + 1) % Polygon.Count];
        double dx = b.Easting - a.Easting;
        double dy = b.Northing - a.Northing;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12) return (new Vec2(1, 0), 0);
        return (new Vec2(dx / len, dy / len), len);
    }

    private static double HeadingFromTangent(Vec2 tangent, bool forward)
    {
        // Heading from +N CW: atan2(dE, dN). Reverse if walking CW.
        double dE = forward ? tangent.Easting : -tangent.Easting;
        double dN = forward ? tangent.Northing : -tangent.Northing;
        double h = Math.Atan2(dE, dN);
        const double TwoPi = 2.0 * Math.PI;
        if (h < 0) h += TwoPi;
        return h;
    }
}
