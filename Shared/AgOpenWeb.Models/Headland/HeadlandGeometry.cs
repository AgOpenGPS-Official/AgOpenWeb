// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Headland;

/// <summary>
/// The headland's geometry: plain functions on the points and numbers they are given, not the
/// open field or the configuration (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md, item 2). Moved from
/// MainViewModel.Headland, MainViewModel.Headland.Remote and MainViewModel unchanged. No state,
/// so any thread. (The largest whole-boundary offset stays in the view model: it uses
/// PolygonOffsetService, in Services.)
/// </summary>
public static class HeadlandGeometry
{
    /// <summary>
    /// Remove self-intersecting loops from an offset polygon.
    /// When the offset is larger than a convex feature (like a fillet), the offset
    /// polygon inverts and crosses itself. This detects those crossings and removes
    /// the inverted loops.
    /// </summary>
    public static List<Vec3> RemoveSelfIntersections(List<Vec3> points)
    {
        if (points.Count < 4) return points;

        var clean = new List<Vec3> { points[0] };
        int i = 0;

        while (i < points.Count - 1)
        {
            // Check if any later edge crosses the current edge
            var a1 = points[i];
            var a2 = points[i + 1];

            int skipTo = -1;
            Vec3 intersectPt = default;

            // Check against all non-adjacent edges ahead
            for (int j = i + 2; j < points.Count - 1; j++)
            {
                var b1 = points[j];
                var b2 = points[j + 1];

                if (LineSegmentIntersection(
                    a1.Easting, a1.Northing, a2.Easting, a2.Northing,
                    b1.Easting, b1.Northing, b2.Easting, b2.Northing,
                    out double t, out double u))
                {
                    if (t > 0.01 && t < 0.99 && u > 0.01 && u < 0.99)
                    {
                        // Self-intersection found - skip the loop
                        double ix = a1.Easting + t * (a2.Easting - a1.Easting);
                        double iy = a1.Northing + t * (a2.Northing - a1.Northing);
                        intersectPt = new Vec3(ix, iy, a1.Heading);
                        skipTo = j + 1;
                        break; // Take first intersection
                    }
                }
            }

            if (skipTo >= 0)
            {
                // Add intersection point and skip the loop
                clean.Add(intersectPt);
                i = skipTo;
            }
            else
            {
                // No intersection, keep the next point
                i++;
                if (i < points.Count)
                    clean.Add(points[i]);
            }
        }

        return clean;
    }

    /// <summary>A point well inside the boundary ring (<see cref="GeometryMath.TryInteriorPoint{T}"/>),
    /// or the vertex average for a ring too thin to hold a grid point.</summary>
    public static (double cx, double cy) FieldInteriorPoint(IReadOnlyList<Models.BoundaryPoint> ring)
    {
        if (GeometryMath.TryInteriorPoint(ring, out double e, out double n)) return (e, n);
        double cx = 0, cy = 0;
        foreach (var bp in ring) { cx += bp.Easting; cy += bp.Northing; }
        return (cx / ring.Count, cy / ring.Count);
    }

    /// <summary>Is <paramref name="offset"/> a closed ring inside <paramref name="ring"/> and
    /// smaller than it, as an inward offset of it is? A point on the edge can test either
    /// way, so most of the points inside is enough.</summary>
    public static bool IsInwardOffsetOf(IReadOnlyList<Vec3> offset, IReadOnlyList<Models.BoundaryPoint> ring)
    {
        if (offset.Count < 4 || ring.Count < 3) return false;
        if (GeometryMath.PolygonArea(offset) >= GeometryMath.PolygonArea(ring)) return false;
        int inside = 0;
        foreach (var p in offset)
            if (GeometryMath.IsPointInPolygon(ring, p.Easting, p.Northing)) inside++;
        return inside * 2 > offset.Count;
    }

    public static List<Vec3> BuildOffsetLineWithExtensions(Models.Headland.HeadlandSegment seg)
    {
        var line = new List<Vec3>();
        if (seg.StartExtension > 0 && seg.OffsetPoints.Count >= 2)
        {
            var s0 = seg.OffsetPoints[0]; var s1 = seg.OffsetPoints[1];
            double sdx = s0.Easting - s1.Easting, sdy = s0.Northing - s1.Northing;
            double slen = System.Math.Sqrt(sdx * sdx + sdy * sdy);
            if (slen > 0.01)
                line.Add(new Vec3(s0.Easting + sdx / slen * seg.StartExtension, s0.Northing + sdy / slen * seg.StartExtension, s0.Heading));
        }
        line.AddRange(seg.OffsetPoints);
        if (seg.EndExtension > 0 && seg.OffsetPoints.Count >= 2)
        {
            var e0 = seg.OffsetPoints[^2]; var e1 = seg.OffsetPoints[^1];
            double edx = e1.Easting - e0.Easting, edy = e1.Northing - e0.Northing;
            double elen = System.Math.Sqrt(edx * edx + edy * edy);
            if (elen > 0.01)
                line.Add(new Vec3(e1.Easting + edx / elen * seg.EndExtension, e1.Northing + edy / elen * seg.EndExtension, e1.Heading));
        }
        return line;
    }

    /// <summary>Intersect two infinite lines. Returns false if parallel.</summary>
    public static bool LineLineIntersection(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy,
        out double ix, out double iy)
    {
        double denom = (bx - ax) * (dy - cy) - (by - ay) * (dx - cx);
        ix = iy = 0;
        if (System.Math.Abs(denom) < 1e-10) return false;
        double t = ((cx - ax) * (dy - cy) - (cy - ay) * (dx - cx)) / denom;
        ix = ax + t * (bx - ax);
        iy = ay + t * (by - ay);
        return true;
    }

    public static double CalculateSignedArea(List<Vec3> polygon)
    {
        double area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % polygon.Count];
            area += (p2.Easting - p1.Easting) * (p2.Northing + p1.Northing);
        }
        return area / 2.0;
    }

    /// <summary>
    /// Find where a line segment (from lineStart toward lineDir) intersects the headland polygon.
    /// Returns the index of the headland segment where intersection occurs, or -1 if no intersection.
    /// </summary>
    public static int FindLineHeadlandIntersection(Vec3 lineStart, Vec3 lineDir, List<Vec3> headland, out Vec3 intersectionPoint)
    {
        double bestDist = double.MaxValue;
        int bestIdx = -1;
        intersectionPoint = default;

        for (int i = 0; i < headland.Count - 1; i++)
        {
            var p1 = headland[i];
            var p2 = headland[i + 1];

            if (LineSegmentIntersection(
                lineStart.Easting, lineStart.Northing, lineDir.Easting, lineDir.Northing,
                p1.Easting, p1.Northing, p2.Easting, p2.Northing,
                out double t, out double u))
            {
                if (t >= 0 && t <= 1) // Segment from lineStart to lineDir (extension tip)
                {
                    // Distance from lineStart to intersection
                    double dx = lineDir.Easting - lineStart.Easting;
                    double dy = lineDir.Northing - lineStart.Northing;
                    double dist = System.Math.Sqrt(dx * dx + dy * dy) * t;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = i;
                        // Compute actual intersection point on the headland segment
                        var hp1 = headland[i];
                        var hp2 = headland[i + 1];
                        intersectionPoint = new Vec3(
                            hp1.Easting + u * (hp2.Easting - hp1.Easting),
                            hp1.Northing + u * (hp2.Northing - hp1.Northing),
                            hp1.Heading);
                    }
                }
            }
        }

        return bestIdx;
    }

    /// <summary>Same as FindLineHeadlandIntersection but returns the FARTHEST intersection from lineStart.</summary>
    public static int FindLineHeadlandIntersectionFarthest(Vec3 lineStart, Vec3 lineDir, List<Vec3> headland, out Vec3 intersectionPoint)
    {
        double bestDist = -1;
        int bestIdx = -1;
        intersectionPoint = default;

        for (int i = 0; i < headland.Count - 1; i++)
        {
            var p1 = headland[i];
            var p2 = headland[i + 1];

            if (LineSegmentIntersection(
                lineStart.Easting, lineStart.Northing, lineDir.Easting, lineDir.Northing,
                p1.Easting, p1.Northing, p2.Easting, p2.Northing,
                out double t, out double u))
            {
                if (t >= 0 && t <= 1)
                {
                    double dx = lineDir.Easting - lineStart.Easting;
                    double dy = lineDir.Northing - lineStart.Northing;
                    double dist = System.Math.Sqrt(dx * dx + dy * dy) * t;
                    if (dist > bestDist)
                    {
                        bestDist = dist;
                        bestIdx = i;
                        intersectionPoint = new Vec3(
                            headland[i].Easting + u * (headland[i + 1].Easting - headland[i].Easting),
                            headland[i].Northing + u * (headland[i + 1].Northing - headland[i].Northing),
                            headland[i].Heading);
                    }
                }
            }
        }

        return bestIdx;
    }

    public static bool LineSegmentIntersection(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy,
        out double t, out double u)
    {
        double denom = (bx - ax) * (dy - cy) - (by - ay) * (dx - cx);
        t = u = 0;
        if (System.Math.Abs(denom) < 1e-10) return false;

        t = ((cx - ax) * (dy - cy) - (cy - ay) * (dx - cx)) / denom;
        u = ((cx - ax) * (by - ay) - (cy - ay) * (bx - ax)) / denom;

        return u >= 0 && u <= 1; // u is the parameter on the headland segment
    }

    /// <summary>Diagonal of the boundary's bounding box — a safe "extend past the field"
    /// length so drawn headland lines overshoot far enough to cross their neighbours.</summary>
    public static double BoundaryBboxDiagonal(
        System.Collections.Generic.IReadOnlyList<AgOpenWeb.Models.BoundaryPoint> pts)
    {
        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach (var p in pts)
        {
            if (p.Easting < minE) minE = p.Easting;
            if (p.Easting > maxE) maxE = p.Easting;
            if (p.Northing < minN) minN = p.Northing;
            if (p.Northing > maxN) maxN = p.Northing;
        }
        double w = maxE - minE, h = maxN - minN;
        double diag = System.Math.Sqrt(w * w + h * h);
        return diag > 1 ? diag : 50.0;
    }

    public static int FindNearestBoundaryVertex(
        System.Collections.Generic.IReadOnlyList<AgOpenWeb.Models.BoundaryPoint> pts, double e, double n)
    {
        double minDist = double.MaxValue;
        int bestIdx = -1;
        for (int i = 0; i < pts.Count; i++)
        {
            double dx = pts[i].Easting - e;
            double dy = pts[i].Northing - n;
            double d = dx * dx + dy * dy;
            if (d < minDist) { minDist = d; bestIdx = i; }
        }
        return bestIdx;
    }

    /// <summary>Boundary points along the shorter arc between two vertex indices.</summary>
    public static List<Vec3> ExtractBoundaryRing(
        System.Collections.Generic.IReadOnlyList<AgOpenWeb.Models.BoundaryPoint> pts, int idx1, int idx2)
    {
        int count = pts.Count;

        var forward = new List<Vec3>();
        int i = idx1;
        while (true)
        {
            var p = pts[i];
            forward.Add(new Vec3(p.Easting, p.Northing, p.Heading));
            if (i == idx2) break;
            i = (i + 1) % count;
            if (forward.Count > count + 1) break;
        }

        var reverse = new List<Vec3>();
        i = idx1;
        while (true)
        {
            var p = pts[i];
            reverse.Add(new Vec3(p.Easting, p.Northing, p.Heading));
            if (i == idx2) break;
            i = (i - 1 + count) % count;
            if (reverse.Count > count + 1) break;
        }

        return forward.Count <= reverse.Count ? forward : reverse;
    }

    /// <summary>
    /// Convert Vec2 preview line to Vec3 with calculated headings
    /// </summary>
    public static List<Models.Base.Vec3>? ConvertPreviewToVec3(List<Models.Base.Vec2>? preview)
    {
        if (preview == null || preview.Count < 3) return null;

        var result = new List<Models.Base.Vec3>(preview.Count);
        for (int i = 0; i < preview.Count; i++)
        {
            double heading;
            if (i == 0)
            {
                double dx = preview[1].Easting - preview[0].Easting;
                double dy = preview[1].Northing - preview[0].Northing;
                heading = System.Math.Atan2(dx, dy);
            }
            else if (i == preview.Count - 1)
            {
                double dx = preview[i].Easting - preview[i - 1].Easting;
                double dy = preview[i].Northing - preview[i - 1].Northing;
                heading = System.Math.Atan2(dx, dy);
            }
            else
            {
                double dx = preview[i + 1].Easting - preview[i - 1].Easting;
                double dy = preview[i + 1].Northing - preview[i - 1].Northing;
                heading = System.Math.Atan2(dx, dy);
            }
            result.Add(new Models.Base.Vec3(preview[i].Easting, preview[i].Northing, heading));
        }
        return result;
    }
}
