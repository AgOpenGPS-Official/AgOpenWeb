// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.Services.Headland;

/// <summary>
/// Building the headland from its segments (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 3): each
/// segment's offset line against the field's outer boundary, and the headland the offset lines
/// cut out of the boundary. Moved from MainViewModel.Headland (ComputeSegmentOffset, and the body
/// of BuildHeadlandFromSegments between the boundary check and applying the result); the
/// segments are FieldHeadland's, applying, showing and saving the result the view model's. The
/// segments' offset points and flags are written here, as before, on the caller's thread (the
/// host loop). Otherwise no state.
/// </summary>
public static class HeadlandSegmentBuilder
{
    /// <summary>
    /// Compute offset points for a headland segment by offsetting boundary points inward.
    /// </summary>
    public static void ComputeSegmentOffset(Models.Headland.HeadlandSegment segment, Models.BoundaryPolygon? outerBoundary)
    {
        if (segment.BoundaryPoints.Count < 2)
        {
            segment.OffsetPoints.Clear();
            return;
        }

        double offset = segment.Offset;

        // For closed polygons (Boundary type or closed curves), use Clipper2 for proper
        // concave handling instead of edge-based offset
        bool isClosed = segment.BoundaryPoints.Count >= 3 &&
            System.Math.Pow(segment.BoundaryPoints[0].Easting - segment.BoundaryPoints[^1].Easting, 2) +
            System.Math.Pow(segment.BoundaryPoints[0].Northing - segment.BoundaryPoints[^1].Northing, 2) < 25.0;

        if ((segment.Type == Models.Headland.HeadlandSegmentType.Boundary || isClosed) && segment.BoundaryPoints.Count >= 3)
        {
            var clipperOffset = new Services.Geometry.PolygonOffsetService();
            var vec2Pts = segment.BoundaryPoints.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            var clipperResult = clipperOffset.CreateInwardOffset(vec2Pts, offset);
            if (clipperResult != null && clipperResult.Count >= 3)
            {
                segment.OffsetPoints = clipperResult.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
                return;
            }
            if (segment.Type == Models.Headland.HeadlandSegmentType.Boundary)
            {
                // The offset swallowed the field: nothing is left inside it. No line, rather
                // than the edge-based fallback's, which lies outside the field and crosses itself.
                segment.OffsetPoints = new List<Vec3>();
                return;
            }
        }

        // Determine offset direction (inward toward field center)
        double sign = 1.0;
        if (segment.Type == Models.Headland.HeadlandSegmentType.Boundary && segment.BoundaryPoints.Count >= 3)
        {
            // Closed polygon: use winding order
            double signedArea = 0;
            for (int j = 0; j < segment.BoundaryPoints.Count; j++)
            {
                var p1 = segment.BoundaryPoints[j];
                var p2 = segment.BoundaryPoints[(j + 1) % segment.BoundaryPoints.Count];
                signedArea += (p2.Easting - p1.Easting) * (p2.Northing + p1.Northing);
            }
            sign = signedArea > 0 ? 1.0 : -1.0;
        }
        else if (segment.BoundaryPoints.Count >= 2)
        {
            // Open segment: determine which side faces the field center
            var boundary = outerBoundary;
            if (boundary?.Points != null && boundary.Points.Count >= 3)
            {
                // A point well inside the field (not the vertex average: on a bent field
                // that lies near the edge, or outside the field)
                var (cx, cy) = HeadlandGeometry.FieldInteriorPoint(boundary.Points);

                // Test offset direction at midpoint
                int mid = segment.BoundaryPoints.Count / 2;
                var midPt = segment.BoundaryPoints[mid];
                var prevPt = mid > 0 ? segment.BoundaryPoints[mid - 1] : segment.BoundaryPoints[mid];
                var nextPt = mid < segment.BoundaryPoints.Count - 1 ? segment.BoundaryPoints[mid + 1] : segment.BoundaryPoints[mid];

                double dx = nextPt.Easting - prevPt.Easting;
                double dy = nextPt.Northing - prevPt.Northing;
                double len = System.Math.Sqrt(dx * dx + dy * dy);
                if (len > 0.001)
                {
                    double nx = dy / len, ny = -dx / len;
                    // Check if offset toward centroid or away
                    double testE = midPt.Easting + nx;
                    double testN = midPt.Northing + ny;
                    double distToCenterOrig = System.Math.Pow(midPt.Easting - cx, 2) + System.Math.Pow(midPt.Northing - cy, 2);
                    double distToCenterTest = System.Math.Pow(testE - cx, 2) + System.Math.Pow(testN - cy, 2);
                    sign = distToCenterTest < distToCenterOrig ? 1.0 : -1.0;
                }
            }
        }

        var result = new List<Vec3>();

        // Edge-based offset: shift each edge by offset distance, then intersect consecutive edges
        // This handles curves correctly (constant distance from boundary)
        var pts = segment.BoundaryPoints;
        int ptCount = pts.Count;

        if (ptCount == 2)
        {
            // Simple line: just shift both points by the perpendicular
            double dx = pts[1].Easting - pts[0].Easting;
            double dy = pts[1].Northing - pts[0].Northing;
            double len = System.Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001) len = 1;
            double nx = sign * dy / len * offset;
            double ny = sign * -dx / len * offset;
            result.Add(new Vec3(pts[0].Easting + nx, pts[0].Northing + ny, pts[0].Heading));
            result.Add(new Vec3(pts[1].Easting + nx, pts[1].Northing + ny, pts[1].Heading));
        }
        else
        {
            // Build offset edges (each edge shifted by offset along its normal)
            var offEdges = new List<(double ax, double ay, double bx, double by)>();
            for (int i = 0; i < ptCount - 1; i++)
            {
                double dx = pts[i + 1].Easting - pts[i].Easting;
                double dy = pts[i + 1].Northing - pts[i].Northing;
                double len = System.Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.001) continue;
                double nx = sign * dy / len * offset;
                double ny = sign * -dx / len * offset;
                offEdges.Add((pts[i].Easting + nx, pts[i].Northing + ny,
                              pts[i + 1].Easting + nx, pts[i + 1].Northing + ny));
            }

            if (offEdges.Count == 0) { segment.OffsetPoints = result; return; }

            // First offset point
            result.Add(new Vec3(offEdges[0].ax, offEdges[0].ay, pts[0].Heading));

            // Intersect consecutive offset edges to find interior offset points
            for (int i = 0; i < offEdges.Count - 1; i++)
            {
                var e1 = offEdges[i];
                var e2 = offEdges[i + 1];

                if (HeadlandGeometry.LineLineIntersection(e1.ax, e1.ay, e1.bx, e1.by,
                                         e2.ax, e2.ay, e2.bx, e2.by,
                                         out double ix, out double iy))
                {
                    result.Add(new Vec3(ix, iy, pts[i + 1].Heading));
                }
                else
                {
                    // Parallel edges - use endpoint of first edge
                    result.Add(new Vec3(e1.bx, e1.by, pts[i + 1].Heading));
                }
            }

            // Last offset point
            var last = offEdges[^1];
            result.Add(new Vec3(last.bx, last.by, pts[^1].Heading));
        }

        // Remove self-intersections (inverted fillets when offset > corner radius)
        // Safe for all types since edge-based offset produces accurate distances
        segment.OffsetPoints = result.Count > 3 ? HeadlandGeometry.RemoveSelfIntersections(result) : result;
    }

    /// <summary>The headland the segments' offset lines cut out of <paramref name="bnd"/>, and how many
    /// cuts were made (none: the boundary itself, closed).</summary>
    public static (List<Vec3> Headland, int CutsApplied) Build(
        IReadOnlyList<HeadlandSegment> segments, Models.BoundaryPolygon bnd, ILogger logger)
    {
        var headland = new List<Vec3>();
        foreach (var pt in bnd.Points)
            headland.Add(new Vec3(pt.Easting, pt.Northing, pt.Heading));
        headland.Add(new Vec3(bnd.Points[0].Easting, bnd.Points[0].Northing, bnd.Points[0].Heading));

        int cutsApplied = 0;

        // Build all offset lines with extensions
        // Track list of segments per chain (merging combines multiple segments)
        var offsetLines = new List<(List<Models.Headland.HeadlandSegment> segs, List<Vec3> line)>();
        foreach (var seg in segments)
        {
            if (seg.OffsetPoints.Count < 2) continue;

            // Detect closed-loop offset (boundary covers full polygon)
            // If first and last boundary points are the same, the offset is a closed polygon
            double bndCloseDist = seg.BoundaryPoints.Count >= 3
                ? System.Math.Sqrt(
                    System.Math.Pow(seg.BoundaryPoints[0].Easting - seg.BoundaryPoints[^1].Easting, 2) +
                    System.Math.Pow(seg.BoundaryPoints[0].Northing - seg.BoundaryPoints[^1].Northing, 2))
                : double.MaxValue;

            if (bndCloseDist < 5.0 && seg.OffsetPoints.Count >= 4)
            {
                // Closed boundary segment - use offset directly as headland polygon
                var closedPoly = new List<Vec3>(seg.OffsetPoints);
                closedPoly.Add(closedPoly[0]); // close the loop

                // A closed inward offset is the headland line when it lies inside the boundary
                // and is smaller than it: an inward offset always is, an outward or broken one
                // never. The old test, "contains the average of the vertices", refused the
                // correct offset of a bent 6 ha field whose vertex average lies 8 m from the
                // edge; the headland then came out of the open-line fallback below, on the
                // edge and crossing itself at 12 m, and not at all at 16 m.
                if (HeadlandGeometry.IsInwardOffsetOf(closedPoly, bnd.Points))
                {
                    headland = closedPoly;
                    seg.IsEffective = true;
                    cutsApplied++;
                    logger.LogDebug($"[Headland] Closed boundary offset '{seg.Name}' used as headland ({closedPoly.Count} pts)");
                    continue;
                }
            }

            var ol = HeadlandGeometry.BuildOffsetLineWithExtensions(seg);
            offsetLines.Add((new List<Models.Headland.HeadlandSegment> { seg }, ol));
        }

        // Pre-check: which offset lines already individually reach the boundary on both ends
        // These should NOT be merged with other lines (merging could break them)
        var reachesBothEnds = new HashSet<int>();
        for (int i = 0; i < offsetLines.Count; i++)
        {
            var line = offsetLines[i].line;
            if (line.Count < 2) continue;
            int halfCk = System.Math.Max(2, line.Count / 2);

            bool startReaches = false;
            for (int oi = 0; oi < System.Math.Min(halfCk, line.Count - 1) && !startReaches; oi++)
                startReaches = HeadlandGeometry.FindLineHeadlandIntersection(line[oi], line[oi + 1], headland, out _) >= 0;

            bool endReaches = false;
            for (int oi = line.Count - 1; oi >= System.Math.Max(1, line.Count - halfCk) && !endReaches; oi--)
                endReaches = HeadlandGeometry.FindLineHeadlandIntersection(line[oi - 1], line[oi], headland, out _) >= 0;

            if (startReaches && endReaches) reachesBothEnds.Add(i);
        }

        // Try to merge chained offset lines that intersect each other
        // This handles the case where two lines each only touch boundary on one side
        // but connect to each other on the other side
        // Skip lines that already reach both boundary ends
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int a = 0; a < offsetLines.Count && !merged; a++)
            {
                if (reachesBothEnds.Contains(a)) continue;
                for (int b = a + 1; b < offsetLines.Count && !merged; b++)
                {
                    if (reachesBothEnds.Contains(b)) continue;
                    // Check if end of A intersects any segment of B
                    var lineA = offsetLines[a].line;
                    var lineB = offsetLines[b].line;

                    // Check segment pairs between A's ends and B for intersection
                    // Search from A's end backward to prefer extending the chain (not cutting it short)
                    Vec3 intersectPt = default;
                    bool found = false;
                    int aSegIdx = -1;
                    for (int ai = lineA.Count - 2; ai >= 0 && !found; ai--)
                    {
                        for (int bi = 0; bi < lineB.Count - 1 && !found; bi++)
                        {
                            if (HeadlandGeometry.LineSegmentIntersection(
                                lineA[ai].Easting, lineA[ai].Northing, lineA[ai + 1].Easting, lineA[ai + 1].Northing,
                                lineB[bi].Easting, lineB[bi].Northing, lineB[bi + 1].Easting, lineB[bi + 1].Northing,
                                out double t, out double u))
                            {
                                if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
                                {
                                    intersectPt = new Vec3(
                                        lineB[bi].Easting + u * (lineB[bi + 1].Easting - lineB[bi].Easting),
                                        lineB[bi].Northing + u * (lineB[bi + 1].Northing - lineB[bi].Northing), 0);
                                    found = true;
                                    aSegIdx = ai;
                                }
                            }
                        }
                    }

                    if (found)
                    {
                        // Trim A at intersection: keep A[0..aSegIdx] + intersectPt
                        var trimmedA = new List<Vec3>();
                        for (int k = 0; k <= aSegIdx; k++)
                            trimmedA.Add(lineA[k]);
                        trimmedA.Add(intersectPt);

                        // Find which segment of B contains the intersection and trim B too
                        int bSegIdx = -1;
                        for (int bi = 0; bi < lineB.Count - 1; bi++)
                        {
                            if (HeadlandGeometry.LineSegmentIntersection(
                                lineA[aSegIdx].Easting, lineA[aSegIdx].Northing, lineA[aSegIdx + 1].Easting, lineA[aSegIdx + 1].Northing,
                                lineB[bi].Easting, lineB[bi].Northing, lineB[bi + 1].Easting, lineB[bi + 1].Northing,
                                out double t2, out double u2))
                            {
                                if (t2 >= 0 && t2 <= 1 && u2 >= 0 && u2 <= 1)
                                {
                                    bSegIdx = bi;
                                    break;
                                }
                            }
                        }

                        // Determine which end of B to connect from and trim at intersection
                        double dBStart = System.Math.Pow(lineB[0].Easting - intersectPt.Easting, 2) + System.Math.Pow(lineB[0].Northing - intersectPt.Northing, 2);
                        double dBEnd = System.Math.Pow(lineB[^1].Easting - intersectPt.Easting, 2) + System.Math.Pow(lineB[^1].Northing - intersectPt.Northing, 2);

                        var combined = new List<Vec3>(trimmedA);
                        if (dBStart < dBEnd)
                        {
                            // B goes forward from intersection: trim B start, keep bSegIdx+1..end
                            if (bSegIdx >= 0)
                                for (int k = bSegIdx + 1; k < lineB.Count; k++)
                                    combined.Add(lineB[k]);
                            else
                                combined.AddRange(lineB);
                        }
                        else
                        {
                            // B goes backward from intersection: trim B end, keep 0..bSegIdx reversed
                            if (bSegIdx >= 0)
                                for (int k = bSegIdx; k >= 0; k--)
                                    combined.Add(lineB[k]);
                            else
                            {
                                var rev = new List<Vec3>(lineB);
                                rev.Reverse();
                                combined.AddRange(rev);
                            }
                        }

                        var mergedSegs = new List<Models.Headland.HeadlandSegment>(offsetLines[a].segs);
                        mergedSegs.AddRange(offsetLines[b].segs);
                        offsetLines[a] = (mergedSegs, combined);
                        offsetLines.RemoveAt(b);
                        merged = true;
                        logger.LogDebug($"[Headland] Merged offset lines: {mergedSegs.Count} segments combined");
                    }
                }
            }
        }

        // Check for closed loops: merged chains whose start and end meet
        // These can divide the polygon without touching the boundary
        // First try to close near-miss loops by finding where end segments intersect start segments
        for (int ci = 0; ci < offsetLines.Count; ci++)
        {
            var (closedSegs, closedLine) = offsetLines[ci];
            if (closedLine.Count < 6 || closedSegs.Count < 2) continue; // Need multiple merged segments

            double loopDist = System.Math.Sqrt(
                System.Math.Pow(closedLine[0].Easting - closedLine[^1].Easting, 2) +
                System.Math.Pow(closedLine[0].Northing - closedLine[^1].Northing, 2));

            if (loopDist >= 5.0)
            {
                // Ends don't meet directly - check if end segments intersect start segments
                // This handles the case where extensions overshoot past the closing corner
                int searchRange = System.Math.Max(3, closedLine.Count / 2);
                Vec3 closeIntersectPt = default;
                int startSegClose = -1, endSegClose = -1;

                for (int si = 0; si < System.Math.Min(searchRange, closedLine.Count / 2) && startSegClose < 0; si++)
                {
                    int endSearchStart = System.Math.Max(closedLine.Count / 2, si + 2);
                    for (int ei = endSearchStart; ei < closedLine.Count - 1 && startSegClose < 0; ei++)
                    {
                        if (HeadlandGeometry.LineSegmentIntersection(
                            closedLine[si].Easting, closedLine[si].Northing, closedLine[si + 1].Easting, closedLine[si + 1].Northing,
                            closedLine[ei].Easting, closedLine[ei].Northing, closedLine[ei + 1].Easting, closedLine[ei + 1].Northing,
                            out double t, out double u))
                        {
                            if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
                            {
                                closeIntersectPt = new Vec3(
                                    closedLine[si].Easting + t * (closedLine[si + 1].Easting - closedLine[si].Easting),
                                    closedLine[si].Northing + t * (closedLine[si + 1].Northing - closedLine[si].Northing), 0);
                                startSegClose = si;
                                endSegClose = ei;
                            }
                        }
                    }
                }

                if (startSegClose >= 0)
                {
                    // Trim both ends at the intersection to close the loop
                    var trimmed = new List<Vec3> { closeIntersectPt };
                    for (int k = startSegClose + 1; k <= endSegClose; k++)
                        trimmed.Add(closedLine[k]);
                    trimmed.Add(closeIntersectPt);

                    offsetLines[ci] = (closedSegs, trimmed);
                    closedLine = trimmed;
                    loopDist = 0; // Now it's a closed loop
                    logger.LogDebug($"[Headland] Closed loop by trimming extensions at intersection ({closeIntersectPt.Easting:F1},{closeIntersectPt.Northing:F1})");
                }
            }

            if (loopDist < 5.0) // Close enough to form a loop
            {
                // This is a closed loop - use it directly as a divider
                var loopPoly = new List<Vec3>(closedLine);
                loopPoly.Add(loopPoly[0]); // close

                // The headland is the boundary MINUS the loop area
                // Keep the part holding the field's interior point
                var (cx, cy) = HeadlandGeometry.FieldInteriorPoint(bnd.Points);
                bool loopHoldsInterior = GeometryMath.IsPointInPolygon(loopPoly, cx, cy);

                if (!loopHoldsInterior)
                {
                    // Loop is away from the interior - headland is unchanged
                    // (the loop is in the headland area, not the working area)
                    foreach (var cs in closedSegs) cs.IsEffective = true;
                    logger.LogDebug($"[Headland] Closed loop found (away from the interior) - headland unchanged");
                }
                else
                {
                    // Loop holds the interior - the loop IS the headland
                    headland = loopPoly;
                    foreach (var cs in closedSegs) cs.IsEffective = true;
                    cutsApplied++;
                    logger.LogDebug($"[Headland] Closed loop holds the interior - used as headland");
                }

                offsetLines.RemoveAt(ci);
                ci--;
            }
        }

        // Process each offset line (possibly merged chains)
        foreach (var (segs, offsetLine) in offsetLines)
        {
            var seg = segs[0]; // Primary segment for logging

            // Find intersection of offset line with headland polygon
            // Search from each end, limited to half the line to avoid finding the wrong end
            int halfCount = System.Math.Max(2, offsetLine.Count / 2);

            int startIntersectIdx = -1;
            Vec3 startIntersectPt = default;
            int startOffsetSegIdx = -1; // Which offset line segment the start intersection is on
            for (int oi = 0; oi < System.Math.Min(halfCount, offsetLine.Count - 1) && startIntersectIdx < 0; oi++)
            {
                startIntersectIdx = HeadlandGeometry.FindLineHeadlandIntersection(offsetLine[oi], offsetLine[oi + 1], headland, out startIntersectPt);
                if (startIntersectIdx >= 0) startOffsetSegIdx = oi;
            }

            int endIntersectIdx = -1;
            Vec3 endIntersectPt = default;
            int endOffsetSegIdx = -1; // Which offset line segment the end intersection is on
            int endStart = System.Math.Max(1, offsetLine.Count - halfCount);
            // Search from end backward, finding the FARTHEST intersection from segment start
            // (opposite of start search which finds closest). This ensures start and end
            // find different intersections even when checking the same offset segment.
            for (int oi = offsetLine.Count - 1; oi >= endStart && endIntersectIdx < 0; oi--)
            {
                endIntersectIdx = HeadlandGeometry.FindLineHeadlandIntersectionFarthest(offsetLine[oi - 1], offsetLine[oi], headland, out endIntersectPt);
                if (endIntersectIdx >= 0) endOffsetSegIdx = oi - 1;
            }

            logger.LogDebug($"[Headland] Segment '{seg.Name}': start intersect={startIntersectIdx}, end intersect={endIntersectIdx}, offsetLine pts={offsetLine.Count}, headland pts={headland.Count}");

            // Check that both ends intersect at different locations
            // Allow same headland segment if intersection points are far apart
            bool intersectionsFarEnough = false;
            if (startIntersectIdx >= 0 && endIntersectIdx >= 0)
            {
                if (startIntersectIdx != endIntersectIdx)
                {
                    intersectionsFarEnough = true;
                }
                else
                {
                    double ptDist = System.Math.Sqrt(
                        System.Math.Pow(startIntersectPt.Easting - endIntersectPt.Easting, 2) +
                        System.Math.Pow(startIntersectPt.Northing - endIntersectPt.Northing, 2));
                    intersectionsFarEnough = ptDist > 1.0; // At least 1m apart
                }
            }
            foreach (var s in segs) s.IsEffective = intersectionsFarEnough;

            if (intersectionsFarEnough)
            {
                // Both ends intersect - split the polygon into two halves
                int count = headland.Count - 1; // exclude closing duplicate

                // The dividing line goes from startIntersectPt along the offset line to endIntersectPt
                // Include all offset line interior points between the two intersection segments
                var divLine = new List<Vec3> { startIntersectPt };
                if (startOffsetSegIdx >= 0 && endOffsetSegIdx >= 0 && endOffsetSegIdx > startOffsetSegIdx)
                {
                    // Add offset line points between the two intersection segments
                    for (int j = startOffsetSegIdx + 1; j <= endOffsetSegIdx; j++)
                        divLine.Add(offsetLine[j]);
                }
                divLine.Add(endIntersectPt);

                var divLineReverse = new List<Vec3>(divLine);
                divLineReverse.Reverse();

                var pathA = new List<Vec3>();
                var pathB = new List<Vec3>();
                int idx;

                if (startIntersectIdx == endIntersectIdx)
                {
                    // Both intersections on the same headland segment
                    // pathA: just the direct connection between the two points
                    pathA.Add(startIntersectPt);
                    pathA.Add(endIntersectPt);

                    // pathB: walk the entire headland polygon
                    pathB.Add(endIntersectPt);
                    idx = (endIntersectIdx + 1) % count;
                    for (int step = 0; step < count; step++)
                    {
                        pathB.Add(headland[idx]);
                        idx = (idx + 1) % count;
                    }
                    pathB.Add(startIntersectPt);
                }
                else
                {
                    // Path A: start at startIntersectPt, walk headland forward to endIntersectPt
                    idx = (startIntersectIdx + 1) % count;
                    pathA.Add(startIntersectPt);
                    while (idx != (endIntersectIdx + 1) % count)
                    {
                        pathA.Add(headland[idx]);
                        idx = (idx + 1) % count;
                        if (pathA.Count > count + 2) break;
                    }
                    pathA.Add(endIntersectPt);

                    // Path B: start at endIntersectPt, walk headland forward to startIntersectPt
                    idx = (endIntersectIdx + 1) % count;
                    pathB.Add(endIntersectPt);
                    while (idx != (startIntersectIdx + 1) % count)
                    {
                        pathB.Add(headland[idx]);
                        idx = (idx + 1) % count;
                        if (pathB.Count > count + 2) break;
                    }
                    pathB.Add(startIntersectPt);
                }

                // Complete each polygon by adding the dividing line
                // pathA + divLineReverse forms polygon A
                // pathB + divLine forms polygon B
                var polyA = new List<Vec3>(pathA);
                polyA.AddRange(divLineReverse);
                var polyB = new List<Vec3>(pathB);
                polyB.AddRange(divLine);

                // Pick the polygon that contains the field centroid (= working area)
                double cx = 0, cy = 0;
                var bndPts = bnd.Points;
                foreach (var bp in bndPts) { cx += bp.Easting; cy += bp.Northing; }
                cx /= bndPts.Count; cy /= bndPts.Count;

                bool aContains = GeometryMath.IsPointInPolygon(polyA, cx, cy);
                bool bContains = GeometryMath.IsPointInPolygon(polyB, cx, cy);

                logger.LogDebug($"[Headland] PathA: {polyA.Count} pts, PathB: {polyB.Count} pts, centroid: ({cx:F1},{cy:F1}), aContains={aContains}, bContains={bContains}");

                List<Vec3> chosen;
                if (aContains && !bContains) chosen = polyA;
                else if (bContains && !aContains) chosen = polyB;
                else chosen = System.Math.Abs(HeadlandGeometry.CalculateSignedArea(polyA)) >= System.Math.Abs(HeadlandGeometry.CalculateSignedArea(polyB)) ? polyA : polyB;
                if (chosen.Count > 0)
                {
                    // Remove consecutive duplicate points
                    for (int d = chosen.Count - 1; d > 0; d--)
                    {
                        double ddx = chosen[d].Easting - chosen[d-1].Easting;
                        double ddy = chosen[d].Northing - chosen[d-1].Northing;
                        if (ddx * ddx + ddy * ddy < 0.01) chosen.RemoveAt(d);
                    }
                    chosen.Add(chosen[0]); // close loop
                }

                headland = chosen;
                cutsApplied++;
            }
        }

        return (headland, cutsApplied);
    }
}
