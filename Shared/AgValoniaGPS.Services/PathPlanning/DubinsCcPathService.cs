// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.
//
// =============================================================================
// Continuous-curvature variant of DubinsPathService. The clothoid-based
// CC-Dubins of Fraichard & Scheuer (2004) and Banzhaf et al. (2017) — wrapped
// by Fields2Cover's dubins_curves_cc — is replaced here with a simpler cubic
// Bezier blend at each segment junction. The Bezier approach is computationally
// cheap and visually equivalent for the small blend distances used in
// agricultural headland turns; it sacrifices analytic G2 continuity (clothoids
// are exactly G2) in exchange for being a few hundred lines instead of a few
// thousand and avoiding Fresnel-integral dependencies.
// =============================================================================

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Services.PathPlanning;

/// <summary>
/// Continuous-curvature Dubins path planner. Like <see cref="DubinsPathService"/>
/// it produces forward-only three-segment paths (RSR, LSL, RSL, LSR, RLR, LRL),
/// but replaces a region around each segment junction with a cubic Bezier blend.
/// This eliminates the curvature step at arc-to-line and arc-to-arc transitions,
/// producing paths the tractor can actually follow without steering jerk.
/// </summary>
public class DubinsCcPathService
{
    private readonly double _turningRadius;
    private readonly double _blendDistance;
    private readonly DubinsPathService _dubins;

    public double TurningRadius => _turningRadius;
    public double BlendDistance => _blendDistance;

    /// <param name="turningRadius">Minimum turning radius in meters (must be &gt; 0).</param>
    /// <param name="blendDistance">
    ///   Arc length removed from each side of every junction and replaced by a
    ///   cubic Bezier blend. Larger values give smoother turns at the cost of
    ///   small geometric deviation. Set to 0 to disable blending (the result
    ///   then matches plain Dubins). Default 0.5 m.
    /// </param>
    public DubinsCcPathService(double turningRadius, double blendDistance = 0.5)
    {
        if (turningRadius <= 0)
            throw new ArgumentException("Turning radius must be > 0", nameof(turningRadius));
        if (blendDistance < 0)
            throw new ArgumentException("Blend distance must be >= 0", nameof(blendDistance));
        _turningRadius = turningRadius;
        _blendDistance = blendDistance;
        _dubins = new DubinsPathService(turningRadius);
    }

    public sealed class CcPath
    {
        public required List<Vec3> Waypoints { get; init; }
        public required double Length { get; init; }
        public required string Type { get; init; }
        public required bool Smoothed { get; init; }
    }

    /// <summary>
    /// Generate the shortest continuous-curvature Dubins path. Returns an empty
    /// path if no Dubins solution exists between the given poses.
    /// </summary>
    public CcPath GeneratePath(Vec3 start, Vec3 goal)
    {
        var pd = _dubins.GetBestPathData(start, goal);
        if (pd == null || pd.PathCoordinates == null || pd.PathCoordinates.Count < 2)
            return new CcPath { Waypoints = new List<Vec3>(), Length = 0.0, Type = "", Smoothed = false };

        // Build Vec3 waypoints with headings derived from finite differences.
        var raw = ToVec3Path(pd.PathCoordinates, start.Heading, goal.Heading);

        // No-blend path-through: matches plain DubinsPathService output.
        if (_blendDistance <= 0)
            return new CcPath
            {
                Waypoints = raw,
                Length = pd.TotalLength,
                Type = pd.PathType.ToString(),
                Smoothed = false,
            };

        // Segment boundaries inside pd.PathCoordinates, computed from the same
        // segment counts that GetTotalPath uses. Layout produced by GetTotalPath:
        //   index 0           : start position
        //   1 .. seg1+1       : segment 1 (seg1+1 points)
        //   seg1+2 .. seg1+seg2+2 : segment 2 (seg2+1 points)
        //   seg1+seg2+3 .. seg1+seg2+seg3+3 : segment 3 (seg3+1 points)
        //   seg1+seg2+seg3+4  : goal
        int seg1 = (int)Math.Floor(pd.Length1 / DubinsPathService.DriveDistance);
        int seg2 = (int)Math.Floor(pd.Length2 / DubinsPathService.DriveDistance);
        int seg3 = (int)Math.Floor(pd.Length3 / DubinsPathService.DriveDistance);
        int junction12 = seg1 + 1;
        int junction23 = seg1 + seg2 + 2;

        var smoothed = ApplyBezierBlends(raw, junction12, junction23);
        return new CcPath
        {
            Waypoints = smoothed,
            Length = ComputePolylineLength(smoothed),
            Type = pd.PathType.ToString(),
            Smoothed = !ReferenceEquals(smoothed, raw),
        };
    }

    private static List<Vec3> ToVec3Path(List<Vec2> coords, double startHeading, double goalHeading)
    {
        // The last entry in `coords` is the goal, snapped from an overshoot
        // (DubinsPathService rounds segment lengths down with Math.Floor and
        // appends the exact goal). The forward delta at the second-to-last
        // index therefore points BACKWARD when the floor truncation overshot —
        // use a back-difference there so the heading reflects the path's
        // actual travel direction.
        int n = coords.Count;
        var raw = new List<Vec3>(n);

        for (int i = 0; i < n - 2; i++)
        {
            double dE = coords[i + 1].Easting - coords[i].Easting;
            double dN = coords[i + 1].Northing - coords[i].Northing;
            double h = (Math.Abs(dE) < 1e-12 && Math.Abs(dN) < 1e-12)
                ? (i == 0 ? startHeading : raw[i - 1].Heading)
                : Math.Atan2(dE, dN);
            raw.Add(new Vec3(coords[i].Easting, coords[i].Northing, 0) { Heading = NormalizeHeading(h) });
        }

        if (n >= 2)
        {
            int j = n - 2;
            // Back-difference for second-to-last: coords[j] - coords[j-1] points forward.
            double h;
            if (j >= 1)
            {
                double dE = coords[j].Easting - coords[j - 1].Easting;
                double dN = coords[j].Northing - coords[j - 1].Northing;
                h = (Math.Abs(dE) < 1e-12 && Math.Abs(dN) < 1e-12)
                    ? (raw.Count > 0 ? raw[^1].Heading : startHeading)
                    : Math.Atan2(dE, dN);
            }
            else
            {
                h = startHeading;
            }
            raw.Add(new Vec3(coords[j].Easting, coords[j].Northing, 0) { Heading = NormalizeHeading(h) });
        }

        raw.Add(new Vec3(coords[^1].Easting, coords[^1].Northing, 0) { Heading = NormalizeHeading(goalHeading) });
        return raw;
    }

    /// <summary>
    /// Replace ±blendSteps waypoints around each junction with a cubic Bezier
    /// whose control points are along the segment tangents. Falls back to the
    /// raw path if blend regions would overlap (segments too short).
    /// </summary>
    private List<Vec3> ApplyBezierBlends(List<Vec3> raw, int junction12, int junction23)
    {
        int blendSteps = Math.Max(1, (int)Math.Round(_blendDistance / DubinsPathService.DriveDistance));
        int n = raw.Count;

        int cut1Start = junction12 - blendSteps;
        int cut1End = junction12 + blendSteps;
        int cut2Start = junction23 - blendSteps;
        int cut2End = junction23 + blendSteps;

        if (cut1Start < 0) cut1Start = 0;
        if (cut2End > n - 1) cut2End = n - 1;

        // Need at least one waypoint between the two blends and outside each end.
        if (cut1Start >= cut1End) return raw;
        if (cut2Start >= cut2End) return raw;
        if (cut1End >= cut2Start) return raw;

        var result = new List<Vec3>(n);
        for (int i = 0; i <= cut1Start; i++) result.Add(raw[i]);
        AppendBezier(result, raw[cut1Start], raw[cut1End]);
        for (int i = cut1End + 1; i < cut2Start; i++) result.Add(raw[i]);
        AppendBezier(result, raw[cut2Start], raw[cut2End]);
        for (int i = cut2End + 1; i < n; i++) result.Add(raw[i]);
        return result;
    }

    /// <summary>
    /// Append a cubic Bezier from p0 (already in result) to p3 (will be appended)
    /// with G1-continuous tangents. Control-point distance uses chord/3, the
    /// canonical heuristic for cubic Beziers approximating curve segments.
    /// </summary>
    private static void AppendBezier(List<Vec3> result, Vec3 p0, Vec3 p3)
    {
        // Forward unit vectors in (E, N) for our heading convention (CW from +N).
        double t0e = Math.Sin(p0.Heading), t0n = Math.Cos(p0.Heading);
        double t3e = Math.Sin(p3.Heading), t3n = Math.Cos(p3.Heading);

        double dEch = p3.Easting - p0.Easting;
        double dNch = p3.Northing - p0.Northing;
        double chord = Math.Sqrt(dEch * dEch + dNch * dNch);
        double d = chord / 3.0;

        double p1e = p0.Easting + d * t0e, p1n = p0.Northing + d * t0n;
        double p2e = p3.Easting - d * t3e, p2n = p3.Northing - d * t3n;

        // Control-polygon length is an upper bound on Bezier arc length.
        double controlLen =
            Math.Sqrt((p1e - p0.Easting) * (p1e - p0.Easting) + (p1n - p0.Northing) * (p1n - p0.Northing)) +
            Math.Sqrt((p2e - p1e) * (p2e - p1e) + (p2n - p1n) * (p2n - p1n)) +
            Math.Sqrt((p3.Easting - p2e) * (p3.Easting - p2e) + (p3.Northing - p2n) * (p3.Northing - p2n));
        int steps = Math.Max(2, (int)Math.Ceiling(controlLen / DubinsPathService.DriveDistance));

        for (int k = 1; k <= steps; k++)
        {
            double t = (double)k / steps;
            double mt = 1.0 - t;
            double w0 = mt * mt * mt;
            double w1 = 3.0 * mt * mt * t;
            double w2 = 3.0 * mt * t * t;
            double w3 = t * t * t;

            double e = w0 * p0.Easting + w1 * p1e + w2 * p2e + w3 * p3.Easting;
            double nrth = w0 * p0.Northing + w1 * p1n + w2 * p2n + w3 * p3.Northing;

            // B'(t) for tangent-based heading — gives smooth heading interpolation.
            double dde = 3.0 * (mt * mt * (p1e - p0.Easting) + 2.0 * mt * t * (p2e - p1e) + t * t * (p3.Easting - p2e));
            double ddn = 3.0 * (mt * mt * (p1n - p0.Northing) + 2.0 * mt * t * (p2n - p1n) + t * t * (p3.Northing - p2n));
            double h = (Math.Abs(dde) < 1e-12 && Math.Abs(ddn) < 1e-12) ? p0.Heading : Math.Atan2(dde, ddn);

            result.Add(new Vec3(e, nrth, 0) { Heading = NormalizeHeading(h) });
        }

        // Force the last point's heading to exactly match the downstream segment.
        int last = result.Count - 1;
        var lp = result[last];
        result[last] = new Vec3(lp.Easting, lp.Northing, 0) { Heading = NormalizeHeading(p3.Heading) };
    }

    private static double ComputePolylineLength(List<Vec3> waypoints)
    {
        double sum = 0;
        for (int i = 1; i < waypoints.Count; i++)
        {
            double de = waypoints[i].Easting - waypoints[i - 1].Easting;
            double dn = waypoints[i].Northing - waypoints[i - 1].Northing;
            sum += Math.Sqrt(de * de + dn * dn);
        }
        return sum;
    }

    private static double NormalizeHeading(double h)
    {
        const double TwoPi = 2.0 * Math.PI;
        h = h % TwoPi;
        if (h < 0) h += TwoPi;
        return h;
    }
}
