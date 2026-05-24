// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

namespace AgValoniaGPS.Models.Base;

/// <summary>
/// Canonical "normalize boundary resolution once at the source" step.
///
/// GPS capture records boundary points at a fixed ~1 m spacing, which for a large
/// irregular field bakes in thousands of points that then propagate, unfiltered,
/// into storage, derived geometry (tram/headland), inside-tests, and rendering.
/// Normalizing collapses that to a resolution-independent representation:
///
///   1. Douglas-Peucker simplification (deviation tolerance <see cref="DefaultToleranceMeters"/>)
///      drops near-collinear runs while preserving curve detail.
///   2. A max-gap densify cap re-inserts points on long straight edges so offsetting
///      and rendering still have intermediate vertices.
///   3. Per-point heading is recomputed as the forward direction (atan2(dE, dN)),
///      matching the offset convention used across the app.
///
/// This is curvature-adaptive (Option 1 in Plans/BOUNDARY_RESOLUTION_NORMALIZATION.md):
/// one deviation tolerance, shape-aware, scale-robust. If real fields ever show the
/// fixed tolerance mis-sized, prefer a hard point-count ceiling (re-run with a larger
/// tolerance when the result exceeds a budget) over an area-scaled tolerance curve.
/// </summary>
public static class BoundaryResolution
{
    /// <summary>Default Douglas-Peucker deviation tolerance (meters).</summary>
    public const double DefaultToleranceMeters = 0.1;

    /// <summary>
    /// Default max gap (meters) before a straight run is re-densified. Generous so
    /// straight edges stay light; guards against pathological huge gaps only.
    /// </summary>
    public const double DefaultMaxGapMeters = 50.0;

    /// <summary>
    /// Normalize a closed boundary ring in place-free fashion (returns a new list).
    /// Input may be open (first != last) representing an implicitly closed polygon;
    /// the output is open in the same convention with recomputed headings.
    /// </summary>
    public static List<BoundaryPoint> Normalize(
        IReadOnlyList<BoundaryPoint> points,
        double toleranceMeters = DefaultToleranceMeters,
        double maxGapMeters = DefaultMaxGapMeters)
    {
        if (points == null || points.Count == 0)
            return new List<BoundaryPoint>();

        // Work on a copy with any duplicate closing point dropped so DP doesn't
        // anchor a redundant vertex.
        var ring = new List<BoundaryPoint>(points);
        if (ring.Count > 1 && Same(ring[0], ring[^1]))
            ring.RemoveAt(ring.Count - 1);

        // Simplify only when there's enough to simplify; always densify long gaps
        // and recompute headings so even small rings get a consistent result.
        var simplified = ring.Count >= 4 ? DouglasPeuckerClosed(ring, toleranceMeters) : ring;
        var densified = DensifyToMaxGap(simplified, maxGapMeters);
        return RecomputeHeadings(densified);
    }

    private static bool Same(BoundaryPoint a, BoundaryPoint b)
    {
        double de = a.Easting - b.Easting;
        double dn = a.Northing - b.Northing;
        return de * de + dn * dn < 1e-6;
    }

    /// <summary>
    /// Iterative Douglas-Peucker for a closed ring. The ring is treated as an open
    /// polyline from index 0 around to index 0; index 0 (the recording start, a real
    /// vertex) is preserved as the seam anchor.
    /// </summary>
    private static List<BoundaryPoint> DouglasPeuckerClosed(List<BoundaryPoint> ring, double tolerance)
    {
        int n = ring.Count;
        var keep = new bool[n];
        keep[0] = true;

        // Split the closed ring at the seam (0) and at the farthest point from 0 so
        // both halves are simplified independently — avoids the degenerate case where
        // start≈end collapses the whole ring.
        int far = 0;
        double farDist = -1;
        for (int i = 1; i < n; i++)
        {
            double d = DistSq(ring[i], ring[0]);
            if (d > farDist) { farDist = d; far = i; }
        }
        keep[far] = true;

        var stack = new Stack<(int start, int end)>();
        stack.Push((0, far));
        stack.Push((far, n)); // n wraps to index 0

        double tolSq = tolerance * tolerance;
        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            if (end - start < 2) continue;

            var a = ring[start];
            var b = ring[end % n];

            double maxDist = -1;
            int maxIdx = -1;
            for (int i = start + 1; i < end; i++)
            {
                double d = PerpDistSq(ring[i], a, b);
                if (d > maxDist) { maxDist = d; maxIdx = i; }
            }

            if (maxIdx >= 0 && maxDist > tolSq)
            {
                keep[maxIdx] = true;
                stack.Push((start, maxIdx));
                stack.Push((maxIdx, end));
            }
        }

        var result = new List<BoundaryPoint>(n);
        for (int i = 0; i < n; i++)
            if (keep[i]) result.Add(ring[i]);

        // A valid polygon needs at least 3 points; fall back to the input ring.
        return result.Count >= 3 ? result : ring;
    }

    /// <summary>
    /// Insert midpoints on any segment longer than <paramref name="maxGap"/> so straight
    /// runs simplified to two points still carry intermediate vertices for offsetting
    /// and rendering. Operates on the closed ring (last segment wraps to index 0).
    /// </summary>
    private static List<BoundaryPoint> DensifyToMaxGap(List<BoundaryPoint> ring, double maxGap)
    {
        if (maxGap <= 0 || ring.Count < 2) return ring;

        var result = new List<BoundaryPoint>(ring.Count);
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            result.Add(a);

            double dist = Math.Sqrt(DistSq(a, b));
            if (dist > maxGap)
            {
                int inserts = (int)(dist / maxGap);
                for (int k = 1; k <= inserts; k++)
                {
                    double t = (double)k / (inserts + 1);
                    result.Add(new BoundaryPoint(
                        a.Easting + (b.Easting - a.Easting) * t,
                        a.Northing + (b.Northing - a.Northing) * t,
                        0));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Recompute each point's heading as the forward direction to the next point
    /// (atan2(dEast, dNorth)), wrapping at the seam. Matches the perpendicular-offset
    /// convention (perpHeading = heading + PI/2) used by tram/headland generation.
    /// </summary>
    private static List<BoundaryPoint> RecomputeHeadings(List<BoundaryPoint> ring)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            var cur = ring[i];
            var next = ring[(i + 1) % n];
            double de = next.Easting - cur.Easting;
            double dn = next.Northing - cur.Northing;
            cur.Heading = (de == 0 && dn == 0) ? cur.Heading : Math.Atan2(de, dn);
        }
        return ring;
    }

    private static double DistSq(BoundaryPoint a, BoundaryPoint b)
    {
        double de = a.Easting - b.Easting;
        double dn = a.Northing - b.Northing;
        return de * de + dn * dn;
    }

    /// <summary>Squared perpendicular distance from p to the line through a and b.</summary>
    private static double PerpDistSq(BoundaryPoint p, BoundaryPoint a, BoundaryPoint b)
    {
        double dx = b.Easting - a.Easting;
        double dy = b.Northing - a.Northing;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12)
            return DistSq(p, a);

        double cross = (p.Easting - a.Easting) * dy - (p.Northing - a.Northing) * dx;
        return (cross * cross) / lenSq;
    }
}
