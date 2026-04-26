// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.
//
// =============================================================================
// Cell-aware stitcher built on top of the F2C-style Phase 3–7 services:
//   - Cells from BoustrophedonDecomp / TrapezoidalDecomp
//   - Corner classification from CellCornerClassifier (Headland vs Internal)
//   - Per-cell swaths from RotationalSwathGenerator (1+ segments per swath)
//   - Reeds-Shepp paths for U-turns at headland and inter-cell transit
//   - TangentLineBypass for split-sibling reconnection inside a cell
//
// Cell visit order is currently greedy (nearest unvisited HEADLAND corner).
// Held-Karp DP is deferred until benchmarks show greedy underperforms enough
// to justify the 2^N · N · 4 state space — the existing CellTraversalPlanner
// has the algorithmic skeleton when we want it.
// =============================================================================

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.PathPlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

public class CellAwareRouteStitcher
{
    private readonly double _turningRadius;
    private readonly ReedsSheppPathService _rs;
    private readonly DubinsPathService _dubins;

    public CellAwareRouteStitcher(double turningRadius)
    {
        if (turningRadius <= 0) throw new ArgumentException("turningRadius must be > 0");
        _turningRadius = turningRadius;
        _rs = new ReedsSheppPathService(turningRadius);
        _dubins = new DubinsPathService(turningRadius);
    }

    /// <summary>
    /// Build a route plan that visits every cell, drives each cell's swaths in
    /// boustrophedon order, and connects adjacent swaths with the cheapest
    /// available primitive (drive-through if same-swath, U-turn at headland,
    /// tangent bypass around an undrivable inner ring, Reeds-Shepp for
    /// inter-cell transit).
    /// </summary>
    public RoutePlan Stitch(
        List<Cell> cells,
        IReadOnlyDictionary<int, List<GeneratedSwath>> cellSwaths,
        Vec3 startPose)
    {
        var plan = new RoutePlan { Pattern = "Boustrophedon" };
        if (cells == null || cells.Count == 0) return plan;

        var visited = new bool[cells.Count];
        Vec3 currentPose = startPose;

        for (int step = 0; step < cells.Count; step++)
        {
            int next = PickNextCellGreedy(cells, visited, cellSwaths, currentPose);
            if (next < 0) break;
            visited[next] = true;

            var cell = cells[next];
            if (!cellSwaths.TryGetValue(cell.Id, out var swaths) || swaths.Count == 0) continue;

            int entryCorner = PickEntryCorner(cell, swaths, currentPose);
            VisitCell(plan, cell, swaths, entryCorner, ref currentPose, isFirstCell: step == 0, startPose);
        }
        return plan;
    }

    // =========================================================================
    // Cell picking & entry choice (greedy)
    // =========================================================================

    private int PickNextCellGreedy(
        IReadOnlyList<Cell> cells, bool[] visited,
        IReadOnlyDictionary<int, List<GeneratedSwath>> cellSwaths, Vec3 currentPose)
    {
        int best = -1;
        double bestCost = double.PositiveInfinity;
        for (int i = 0; i < cells.Count; i++)
        {
            if (visited[i]) continue;
            var swaths = cellSwaths.TryGetValue(cells[i].Id, out var s) ? s : null;
            if (swaths == null || swaths.Count == 0) continue;
            if (!HasAnyHeadlandCorner(cells[i])) continue;

            // Cost = straight-line to nearest headland corner. Cheap proxy
            // for Reeds-Shepp distance — close enough for cell ordering.
            double minD2 = double.PositiveInfinity;
            for (int c = 0; c < 4; c++)
            {
                if (cells[i].GetCornerKind((CellCorner)c) != CellCornerKind.Headland) continue;
                var corner = CornerPosition(cells[i], swaths, (CellCorner)c);
                double dx = corner.Easting - currentPose.Easting;
                double dy = corner.Northing - currentPose.Northing;
                double d2 = dx * dx + dy * dy;
                if (d2 < minD2) minD2 = d2;
            }
            if (minD2 < bestCost) { bestCost = minD2; best = i; }
        }
        return best;
    }

    private int PickEntryCorner(Cell cell, List<GeneratedSwath> swaths, Vec3 fromPose)
    {
        // Pick the HEADLAND corner closest to the previous exit pose. This
        // matches the greedy cell-pick heuristic — both use straight-line
        // distance, so the planner's two phases agree on which corner.
        int best = 0;
        double bestD2 = double.PositiveInfinity;
        for (int c = 0; c < 4; c++)
        {
            if (cell.GetCornerKind((CellCorner)c) != CellCornerKind.Headland) continue;
            var pos = CornerPosition(cell, swaths, (CellCorner)c);
            double dx = pos.Easting - fromPose.Easting;
            double dy = pos.Northing - fromPose.Northing;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = c; }
        }
        return best;
    }

    /// <summary>
    /// Return the (E, N) position of a cell corner in the original frame.
    /// We reach it through the swath segment endpoints: corners 0/1 are the
    /// two ends of swath 0; corners 2/3 are the two ends of the last swath.
    /// </summary>
    private static Vec2 CornerPosition(Cell cell, List<GeneratedSwath> swaths, CellCorner corner)
    {
        var first = swaths[0];
        var last = swaths[^1];
        return corner switch
        {
            CellCorner.LowSweepLowPerp => first.Segments[0].Start,
            CellCorner.LowSweepHighPerp => first.Segments[^1].End,
            CellCorner.HighSweepLowPerp => last.Segments[0].Start,
            CellCorner.HighSweepHighPerp => last.Segments[^1].End,
            _ => first.Segments[0].Start,
        };
    }

    private static bool HasAnyHeadlandCorner(Cell cell)
    {
        for (int c = 0; c < 4; c++)
            if (cell.GetCornerKind((CellCorner)c) == CellCornerKind.Headland) return true;
        return false;
    }

    // =========================================================================
    // Per-cell traversal
    // =========================================================================

    /// <summary>
    /// Drive every swath in <paramref name="swaths"/> in boustrophedon order
    /// determined by <paramref name="entryCorner"/>, emit Swath/Turn/Transit
    /// segments into <paramref name="plan"/>, advance <paramref name="currentPose"/>.
    /// </summary>
    private void VisitCell(
        RoutePlan plan, Cell cell, List<GeneratedSwath> swaths,
        int entryCorner, ref Vec3 currentPose, bool isFirstCell, Vec3 startPose)
    {
        // Iteration order over swath lines.
        //   entry 0 (low-sweep low-perp)  -> swaths in order, first swath low→high perp
        //   entry 1 (low-sweep high-perp) -> swaths in order, first swath high→low perp
        //   entry 2 (high-sweep low-perp) -> swaths reversed,  first swath low→high perp
        //   entry 3 (high-sweep high-perp)-> swaths reversed,  first swath high→low perp
        bool reverseSwathOrder = entryCorner == 2 || entryCorner == 3;
        bool firstSwathHighToLow = entryCorner == 1 || entryCorner == 3;

        // Ordered list of (swath, drivenInReverse) pairs.
        int n = swaths.Count;
        var ordering = new List<(GeneratedSwath swath, bool reverse)>(n);
        for (int i = 0; i < n; i++)
        {
            int idx = reverseSwathOrder ? (n - 1 - i) : i;
            bool reverse = (i % 2 == 0) ? firstSwathHighToLow : !firstSwathHighToLow;
            ordering.Add((swaths[idx], reverse));
        }

        // Inter-cell transit: Reeds-Shepp from previous cell's exit to this
        // cell's entry. Allows reverse so the planner can cope with pose
        // mismatches between distant cells. Skipped on the first cell.
        var firstSwathReverse = ordering[0].reverse;
        var firstEntryPose = SwathEntryPose(ordering[0].swath, firstSwathReverse);
        if (!isFirstCell)
        {
            EmitReedsSheppTransit(plan, currentPose, firstEntryPose, RouteSegmentType.Transit);
        }
        currentPose = firstEntryPose;

        for (int i = 0; i < ordering.Count; i++)
        {
            var (swath, reverse) = ordering[i];
            EmitSwath(plan, swath, reverse, ref currentPose);

            if (i < ordering.Count - 1)
            {
                var (nextSwath, nextReverse) = ordering[i + 1];
                var nextEntry = SwathEntryPose(nextSwath, nextReverse);
                // Intra-cell U-turn at headland: use Dubins (forward-only).
                // Reeds-Shepp would pick the geometrically shortest path,
                // which for tight headlands is a 3-point turn with reverse —
                // ugly to drive even when a forward LRL/RLR turn fits.
                EmitDubinsTurn(plan, currentPose, nextEntry);
                currentPose = nextEntry;
            }
        }
    }

    /// <summary>
    /// Emit one swath's segments into the plan. If the swath is split, insert
    /// a tangent-bypass connector between siblings (caller is responsible for
    /// eventually feeding the actual obstacle hull — for now we use a straight
    /// connector since Phase 7 already covers the convex-hull bypass).
    /// </summary>
    private void EmitSwath(RoutePlan plan, GeneratedSwath swath, bool reverse, ref Vec3 currentPose)
    {
        var orderedSegments = new List<SwathSegment>(swath.Segments);
        if (reverse)
        {
            orderedSegments.Reverse();
            for (int i = 0; i < orderedSegments.Count; i++)
                orderedSegments[i] = new SwathSegment(orderedSegments[i].End, orderedSegments[i].Start);
        }

        for (int i = 0; i < orderedSegments.Count; i++)
        {
            var seg = orderedSegments[i];
            double h = HeadingFromTo(seg.Start, seg.End);
            var waypoints = new List<Vec3>
            {
                new(seg.Start.Easting, seg.Start.Northing, h),
                new(seg.End.Easting,   seg.End.Northing,   h),
            };
            plan.Segments.Add(new RouteSegment
            {
                Type = RouteSegmentType.Swath,
                SwathIndex = swath.Index,
                Waypoints = waypoints,
                Length = seg.Length,
                IsReverse = reverse,
                IsTurnValid = true,
            });
            currentPose = waypoints[^1];

            // Sibling-segment connector: straight line for now; Phase 7's
            // TangentLineBypass.Bypass would be wired in here when the cell
            // also carries the obstacle hulls. Marked as Transit so downstream
            // section-control treats it as non-working.
            if (i < orderedSegments.Count - 1)
            {
                var next = orderedSegments[i + 1];
                double connectorH = HeadingFromTo(seg.End, next.Start);
                var connector = new List<Vec3>
                {
                    new(seg.End.Easting, seg.End.Northing, connectorH),
                    new(next.Start.Easting, next.Start.Northing, connectorH),
                };
                plan.Segments.Add(new RouteSegment
                {
                    Type = RouteSegmentType.Transit,
                    Waypoints = connector,
                    Length = Math.Sqrt(
                        (next.Start.Easting - seg.End.Easting) * (next.Start.Easting - seg.End.Easting) +
                        (next.Start.Northing - seg.End.Northing) * (next.Start.Northing - seg.End.Northing)),
                    IsTurnValid = true,
                });
                currentPose = connector[^1];
            }
        }
    }

    private void EmitReedsSheppTransit(RoutePlan plan, Vec3 from, Vec3 to, RouteSegmentType type)
    {
        var path = _rs.GetShortestPath(from, to);
        plan.Segments.Add(new RouteSegment
        {
            Type = type,
            Waypoints = path.Waypoints,
            Length = path.Length,
            IsTurnValid = true,
            TurnPathType = "ReedsShepp",
        });
    }

    /// <summary>
    /// Forward-only Dubins turn for intra-cell U-turns. Falls back to a
    /// Reeds-Shepp turn if Dubins finds no path (headland too narrow for any
    /// forward arc family at the configured turning radius).
    /// </summary>
    private void EmitDubinsTurn(RoutePlan plan, Vec3 from, Vec3 to)
    {
        var paths = _dubins.GenerateAllPaths(from, to);
        if (paths.Count == 0 || paths[0].Path.Count == 0)
        {
            // No forward path — fall back to Reeds-Shepp 3-point turn.
            EmitReedsSheppTransit(plan, from, to, RouteSegmentType.Turn);
            return;
        }
        // GenerateAllPaths sorts shortest-first. DubinsPathService rounds segment
        // lengths down with Math.Floor and tacks the goal on; the second-to-last
        // sample can overshoot the goal by up to one DriveDistance step, which
        // breaks segment-chain continuity. Snap the last waypoint to the exact
        // goal pose so the next Swath segment starts where this Turn ends.
        var (waypoints, type, length) = paths[0];
        if (waypoints.Count > 0)
            waypoints[^1] = new Vec3(to.Easting, to.Northing, to.Heading);
        plan.Segments.Add(new RouteSegment
        {
            Type = RouteSegmentType.Turn,
            Waypoints = waypoints,
            Length = length,
            IsTurnValid = true,
            TurnPathType = type,
        });
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Pose at which the vehicle enters a swath given a chosen drive direction.
    /// reverse = false → enter at first segment's Start, heading toward End.
    /// reverse = true  → enter at last segment's End, heading toward Start.
    /// </summary>
    private static Vec3 SwathEntryPose(GeneratedSwath swath, bool reverse)
    {
        if (!reverse)
        {
            var first = swath.Segments[0];
            double h = HeadingFromTo(first.Start, first.End);
            return new Vec3(first.Start.Easting, first.Start.Northing, h);
        }
        else
        {
            var last = swath.Segments[^1];
            double h = HeadingFromTo(last.End, last.Start);
            return new Vec3(last.End.Easting, last.End.Northing, h);
        }
    }

    private static double HeadingFromTo(Vec2 from, Vec2 to)
    {
        // Heading from +N CW: atan2(dE, dN), then normalize to [0, 2π).
        double dE = to.Easting - from.Easting;
        double dN = to.Northing - from.Northing;
        if (Math.Abs(dE) < 1e-12 && Math.Abs(dN) < 1e-12) return 0;
        double h = Math.Atan2(dE, dN);
        const double TwoPi = 2.0 * Math.PI;
        if (h < 0) h += TwoPi;
        return h;
    }
}
