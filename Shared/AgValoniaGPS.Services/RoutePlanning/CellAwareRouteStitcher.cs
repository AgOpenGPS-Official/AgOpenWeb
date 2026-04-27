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
    private IReadOnlyList<HeadlandLoop> _headlandLoops = Array.Empty<HeadlandLoop>();
    private IReadOnlyList<List<Vec2>> _expandedInnerRings = Array.Empty<List<Vec2>>();

    public CellAwareRouteStitcher(double turningRadius)
    {
        if (turningRadius <= 0) throw new ArgumentException("turningRadius must be > 0");
        _turningRadius = turningRadius;
        _rs = new ReedsSheppPathService(turningRadius);
        _dubins = new DubinsPathService(turningRadius);
    }

    /// <summary>
    /// Build a route plan in the snake-with-obstacle pattern:
    /// (1) drive the outer headland loops first (greedy nearest-first among them),
    /// (2) drive the interior cells in sequential sweep order (W→E or per the
    ///     boustrophedon decomposition's natural ordering),
    /// (3) when the next cell to visit touches an inner ring whose coverage
    ///     hasn't been driven yet, drive that ring's headland loops first,
    ///     then proceed into the cell.
    /// Falls back to direct Dubins for transits when no headland loops are
    /// supplied.
    ///
    /// <paramref name="direction"/> is currently unused (the snake pattern
    /// fixes the order); kept for API compatibility.
    /// </summary>
    public RoutePlan Stitch(
        List<Cell> cells,
        IReadOnlyDictionary<int, List<GeneratedSwath>> cellSwaths,
        Vec3 startPose,
        IReadOnlyList<HeadlandLoop>? headlandLoops = null,
        OperationDirection direction = OperationDirection.InputFlow,
        IReadOnlyList<List<Vec2>>? expandedInnerRings = null)
    {
        _headlandLoops = headlandLoops ?? Array.Empty<HeadlandLoop>();
        _expandedInnerRings = expandedInnerRings ?? Array.Empty<List<Vec2>>();
        var plan = new RoutePlan { Pattern = "Boustrophedon" };
        if (cells == null || cells.Count == 0 && _headlandLoops.Count == 0) return plan;

        Vec3 currentPose = startPose;

        // 1. Outer headland coverage first.
        EmitOuterCoverage(plan, ref currentPose);

        // 2. Interior cells in sequential sweep order (cells are pre-sorted
        //    by SweepStart in BoustrophedonDecomp). Inner coverage is
        //    triggered lazily when a cell touching an unvisited inner ring is
        //    about to be entered.
        EmitInteriorCellsSequential(plan, cells, cellSwaths, ref currentPose, startPose);

        return plan;
    }

    /// <summary>
    /// Build a route plan from PRE-CLUSTERED BLOCKS (Hameed 2013 / Höffmann
    /// 2024 §5.7). This sidesteps cellular decomposition: parallel tracks are
    /// generated across the whole field, clipped at obstacles, and grouped
    /// into blocks of contiguous obstacle-free segments. Each block is driven
    /// as a simple back-and-forth boustrophedon. Inner-ring coverage triggers
    /// when first entering a ring-adjacent block. Inter-block transits use
    /// the headland-routed transit machinery.
    /// </summary>
    public RoutePlan StitchBlocks(
        List<Block> blocks,
        Vec3 startPose,
        IReadOnlyList<HeadlandLoop>? headlandLoops = null,
        IReadOnlyList<List<Vec2>>? expandedInnerRings = null)
    {
        _headlandLoops = headlandLoops ?? Array.Empty<HeadlandLoop>();
        _expandedInnerRings = expandedInnerRings ?? Array.Empty<List<Vec2>>();
        var plan = new RoutePlan { Pattern = "BlockClustered" };
        if (blocks == null || blocks.Count == 0 && _headlandLoops.Count == 0) return plan;

        Vec3 currentPose = startPose;

        // 1. Outer headland coverage first.
        EmitOuterCoverage(plan, ref currentPose);

        // 2. Each block as a single back-and-forth boustrophedon, with
        //    inner-ring coverage triggered when first entering a ring-touching
        //    block.
        EmitBlocks(plan, blocks ?? new List<Block>(), ref currentPose, startPose);

        return plan;
    }

    // =========================================================================
    // Phase orchestration: outer-coverage → sequential cells (with inner
    //                     coverage triggered when an inner ring is first hit)
    // =========================================================================

    /// <summary>
    /// Drive every outer headland loop in greedy nearest-first order from
    /// <paramref name="currentPose"/>.
    /// </summary>
    private void EmitOuterCoverage(RoutePlan plan, ref Vec3 currentPose)
    {
        var remaining = new List<HeadlandLoop>();
        foreach (var l in _headlandLoops)
            if (l.Kind == HeadlandLoopKind.Outer) remaining.Add(l);
        DriveLoopsGreedy(plan, ref currentPose, remaining);
    }

    /// <summary>
    /// Drive every inner headland loop belonging to ring index
    /// <paramref name="ringIndex"/>, in greedy nearest-first order.
    /// </summary>
    private void EmitInnerCoverage(RoutePlan plan, ref Vec3 currentPose, int ringIndex)
    {
        var remaining = new List<HeadlandLoop>();
        foreach (var l in _headlandLoops)
            if (l.Kind == HeadlandLoopKind.Inner && l.RingIndex == ringIndex) remaining.Add(l);
        DriveLoopsGreedy(plan, ref currentPose, remaining);
    }

    private void DriveLoopsGreedy(RoutePlan plan, ref Vec3 currentPose, List<HeadlandLoop> remaining)
    {
        while (remaining.Count > 0)
        {
            HeadlandLoop loop = remaining[0];
            double bestD2 = double.PositiveInfinity;
            for (int i = 0; i < remaining.Count; i++)
            {
                var proj = remaining[i].Project(new Vec2(currentPose.Easting, currentPose.Northing));
                double dx = proj.Position.Easting - currentPose.Easting;
                double dy = proj.Position.Northing - currentPose.Northing;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; loop = remaining[i]; }
            }
            remaining.Remove(loop);
            DriveOneLoop(plan, ref currentPose, loop);
        }
    }

    private void DriveOneLoop(RoutePlan plan, ref Vec3 currentPose, HeadlandLoop loop)
    {
        var startProj = loop.Project(new Vec2(currentPose.Easting, currentPose.Northing));
        bool forward = true;
        LoopProjection actualStart = startProj;
        if (plan.Segments.Count > 0)
        {
            LoopProjection fwdAligned = AlignTangentToHeading(loop, startProj, currentPose.Heading,
                forward: true, searchForward: true);
            LoopProjection bwdAligned = AlignTangentToHeading(loop, startProj, currentPose.Heading,
                forward: false, searchForward: true);
            var fwdPose = new Vec3(fwdAligned.Position.Easting, fwdAligned.Position.Northing,
                HeadingFromTangent(fwdAligned.Tangent, forward: true));
            var bwdPose = new Vec3(bwdAligned.Position.Easting, bwdAligned.Position.Northing,
                HeadingFromTangent(bwdAligned.Tangent, forward: false));
            double fwdCost = DubinsLengthOrInfinity(currentPose, fwdPose);
            double bwdCost = DubinsLengthOrInfinity(currentPose, bwdPose);
            forward = fwdCost <= bwdCost;
            var entryPose = forward ? fwdPose : bwdPose;
            actualStart = forward ? fwdAligned : bwdAligned;
            var spliceWp = new List<Vec3>();
            double spliceLen = AppendDubinsSpliceTracked(spliceWp, currentPose, entryPose, out bool sane);
            plan.Segments.Add(new RouteSegment
            {
                Type = RouteSegmentType.Transit,
                Waypoints = spliceWp,
                Length = spliceLen,
                IsTurnValid = sane,
                TurnPathType = "DubinsSplice",
            });
            currentPose = spliceWp[^1];
        }

        var loopWaypoints = WalkFullLoop(loop, actualStart, forward);
        plan.Segments.Add(new RouteSegment
        {
            Type = RouteSegmentType.Swath,
            Waypoints = loopWaypoints,
            Length = loop.Perimeter,
            IsTurnValid = true,
        });
        currentPose = loopWaypoints[^1];
    }

    private void EmitInteriorCellsSequential(
        RoutePlan plan, List<Cell> cells,
        IReadOnlyDictionary<int, List<GeneratedSwath>> cellSwaths,
        ref Vec3 currentPose, Vec3 startPose)
    {
        if (cells == null || cells.Count == 0) return;

        // Tracks which inner rings have already had their coverage driven.
        // Cells abutting an uncovered inner ring trigger coverage of that
        // ring before the cell is processed.
        var coveredRings = new HashSet<int>();

        bool isFirst = (plan.Segments.Count == 0);
        for (int step = 0; step < cells.Count; step++)
        {
            var cell = cells[step]; // already in SweepStart order from BoustrophedonDecomp
            if (!cellSwaths.TryGetValue(cell.Id, out var swaths) || swaths.Count == 0) continue;

            // Trigger inner coverage for any inner ring this cell touches.
            foreach (int ringIdx in InnerRingsTouchedByCell(cell))
            {
                if (coveredRings.Contains(ringIdx)) continue;
                EmitInnerCoverage(plan, ref currentPose, ringIdx);
                coveredRings.Add(ringIdx);
            }

            int entryCorner = PickEntryCorner(cell, swaths, currentPose);
            VisitCell(plan, cell, swaths, entryCorner, ref currentPose,
                isFirstCell: isFirst && step == 0, startPose);
        }
    }

    /// <summary>
    /// Identify which inner ring(s) <paramref name="cell"/> abuts. Checks
    /// every cell-polygon vertex against every expanded inner ring's edges;
    /// any vertex within 5cm of a ring edge counts as "touching." This is
    /// more robust than relying on the bbox-corner classifier, which only
    /// looks at the four bounding-box-closest vertices and can miss a ring
    /// touch when the closest vertex to a bbox corner happens to be a cut
    /// endpoint (raycast intersection) rather than a ring vertex.
    /// </summary>
    private IEnumerable<int> InnerRingsTouchedByCell(Cell cell)
    {
        if (cell.Polygon == null || cell.Polygon.Count == 0 || _expandedInnerRings.Count == 0)
            yield break;

        const double tolerance = 0.05;
        for (int r = 0; r < _expandedInnerRings.Count; r++)
        {
            var ring = _expandedInnerRings[r];
            if (ring == null || ring.Count < 3) continue;
            bool touches = false;
            foreach (var v in cell.Polygon)
            {
                int n = ring.Count;
                for (int i = 0; i < n; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % n];
                    if (GeometryMath.PointToSegmentDistance(v, a, b) <= tolerance)
                    {
                        touches = true;
                        break;
                    }
                }
                if (touches) break;
            }
            if (touches) yield return r;
        }
    }

    /// <summary>Length of the best forward Dubins path, or +∞ if none exists.</summary>
    private double DubinsLengthOrInfinity(Vec3 from, Vec3 to)
    {
        var pd = _dubins.GetBestPathData(from, to);
        return pd?.TotalLength ?? double.PositiveInfinity;
    }

    // =========================================================================
    // Block-clustered path (Hameed 2013)
    // =========================================================================

    /// <summary>
    /// Drive each block's tracks in boustrophedon order. Inner-ring coverage
    /// triggers the first time a block touching that ring is about to be
    /// entered. Inter-block transits use <see cref="EmitHeadlandRoutedTransit"/>.
    /// </summary>
    private void EmitBlocks(
        RoutePlan plan, List<Block> blocks,
        ref Vec3 currentPose, Vec3 startPose)
    {
        if (blocks.Count == 0) return;
        var coveredRings = new HashSet<int>();
        bool isFirst = (plan.Segments.Count == 0);

        for (int blockIdx = 0; blockIdx < blocks.Count; blockIdx++)
        {
            var block = blocks[blockIdx];
            if (block.Tracks.Count == 0) continue;

            // Trigger inner-ring coverage if this block touches a ring whose
            // coverage hasn't been driven yet.
            if (block.InnerRingIndex >= 0 && !coveredRings.Contains(block.InnerRingIndex))
            {
                EmitInnerCoverage(plan, ref currentPose, block.InnerRingIndex);
                coveredRings.Add(block.InnerRingIndex);
            }

            DriveBlockBoustrophedon(plan, block, ref currentPose,
                isFirstThing: isFirst && blockIdx == 0 && plan.Segments.Count == 0);
        }
    }

    /// <summary>
    /// Drive one block. When opWidth/2 ≥ UTurnRadius, walks tracks in straight
    /// boustrophedon order (1, 2, 3, ..., N). Otherwise the perpendicular
    /// offset between consecutive tracks isn't wide enough for the vehicle
    /// to U-turn, so a SKIP-ROW pattern is used: drive every k-th track
    /// (where k = ⌈2·R/opWidth⌉) in pass 1, then return for tracks 1..k-1
    /// in subsequent passes. This makes each intra-block U-turn span k·opWidth
    /// perpendicular distance — radius k·opWidth/2 ≥ R, so the arc fits the
    /// vehicle. Standard agricultural pattern (Hameed §2.1.3 / Höffmann §3).
    /// </summary>
    private void DriveBlockBoustrophedon(
        RoutePlan plan, Block block, ref Vec3 currentPose, bool isFirstThing)
    {
        var tracks = new List<BlockTrack>(block.Tracks);
        tracks.Sort((a, b) => a.SweepCoord.CompareTo(b.SweepCoord));
        int n = tracks.Count;
        if (n == 0) return;

        // Estimate opWidth from the spacing between consecutive tracks. If
        // we have more than one track in the block, the difference in their
        // sweep coords is the (perpendicular) spacing.
        double opWidth = (n >= 2)
            ? Math.Abs(tracks[1].SweepCoord - tracks[0].SweepCoord)
            : 1.0;
        if (opWidth <= 1e-6) opWidth = 1.0;

        // Skip-row factor: how many tracks to skip per pass so the perpendicular
        // offset between consecutively-driven tracks is ≥ 2·R (so radius ≥ R).
        int k = Math.Max(1, (int)Math.Ceiling(2.0 * _turningRadius / opWidth));

        // Build the drive order: pass 0 = tracks 0, k, 2k, ...; pass 1 = tracks 1, k+1, ...; etc.
        var driveOrder = new List<int>(n);
        for (int p = 0; p < k; p++)
        {
            for (int idx = p; idx < n; idx += k)
                driveOrder.Add(idx);
        }

        // Decide which end of the FIRST driven track to enter.
        var first = tracks[driveOrder[0]];
        double dStart2 = Sq(first.Start.Easting - currentPose.Easting)
                       + Sq(first.Start.Northing - currentPose.Northing);
        double dEnd2 = Sq(first.End.Easting - currentPose.Easting)
                     + Sq(first.End.Northing - currentPose.Northing);
        bool entryAtStart = dStart2 <= dEnd2;

        for (int orderIdx = 0; orderIdx < driveOrder.Count; orderIdx++)
        {
            int trackIdx = driveOrder[orderIdx];
            var t = tracks[trackIdx];
            // Alternate drive direction per consecutive driven track.
            bool driveFromStart = (orderIdx % 2 == 0) ? entryAtStart : !entryAtStart;
            var (segStart, segEnd) = driveFromStart ? (t.Start, t.End) : (t.End, t.Start);
            double h = HeadingFromTo(segStart, segEnd);
            var entryPose = new Vec3(segStart.Easting, segStart.Northing, h);

            // Transit from current pose to track entry.
            //   - First track of the very first block: skip (downstream Pure
            //     Pursuit handles "drive to first waypoint").
            //   - First track of a subsequent block: headland-routed transit.
            //   - Pass-boundary transit (start of a new skip-row pass): also
            //     headland-routed (the jump from end-of-pass back to next start
            //     is too far for an intra-block U-turn primitive).
            //   - Other intra-block transitions: semicircle / Dubins U-turn.
            if (orderIdx == 0)
            {
                if (!isFirstThing)
                    EmitHeadlandRoutedTransit(plan, currentPose, entryPose);
            }
            else if (k > 1 && orderIdx % ((n + k - 1) / k) == 0)
            {
                // Pass boundary in skip-row mode — long-distance jump back.
                EmitHeadlandRoutedTransit(plan, currentPose, entryPose);
            }
            else
            {
                if (!TryEmitSemicircleUTurn(plan, currentPose, entryPose))
                    EmitDubinsTurn(plan, currentPose, entryPose);
            }

            // Drive the track segment.
            var waypoints = new List<Vec3>
            {
                new(segStart.Easting, segStart.Northing, h),
                new(segEnd.Easting,   segEnd.Northing,   h),
            };
            double length = Math.Sqrt(Sq(segEnd.Easting - segStart.Easting)
                                     + Sq(segEnd.Northing - segStart.Northing));
            plan.Segments.Add(new RouteSegment
            {
                Type = RouteSegmentType.Swath,
                SwathIndex = t.SwathIndex,
                Waypoints = waypoints,
                Length = length,
                IsReverse = !driveFromStart,
                IsTurnValid = true,
            });
            currentPose = waypoints[^1];
        }
    }

    private static double Sq(double x) => x * x;

    /// <summary>
    /// Sample the entire loop, starting and ending at <paramref name="start"/>.
    /// <paramref name="forward"/> = true walks CCW; false walks CW. The walk
    /// direction must match the tangent direction at the start pose so that
    /// the splice into the loop is smooth (no 180° heading flip at handoff).
    /// </summary>
    private static List<Vec3> WalkFullLoop(HeadlandLoop loop, LoopProjection start, bool forward)
    {
        // Walk from start back to start in the chosen direction. Need a
        // non-degenerate intermediate point or Walk(from=to) returns just
        // a degenerate sample; use the antipodal projection.
        var halfway = ProjectionAtArcLength(loop, (start.ArcLength + loop.Perimeter * 0.5) % loop.Perimeter);
        var part1 = loop.Walk(start, halfway, forward, maxStep: 1.0);
        var part2 = loop.Walk(halfway, start, forward, maxStep: 1.0);
        var combined = new List<Vec3>(part1.Count + part2.Count);
        combined.AddRange(part1);
        for (int i = 1; i < part2.Count; i++) combined.Add(part2[i]);
        return combined;
    }

    /// <summary>Construct a LoopProjection at a specific forward arc length.</summary>
    private static LoopProjection ProjectionAtArcLength(HeadlandLoop loop, double arc)
    {
        // Re-project the point at this arc length onto the loop. Cheap because
        // we just walk the polygon edges in order until we find the right one.
        // (HeadlandLoop doesn't expose the cum array; use Project on the
        // approximate position.)
        // First, find the polygon edge containing this arc length.
        double cum = 0;
        int n = loop.Polygon.Count;
        for (int i = 0; i < n; i++)
        {
            var a = loop.Polygon[i];
            var b = loop.Polygon[(i + 1) % n];
            double dx = b.Easting - a.Easting;
            double dy = b.Northing - a.Northing;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (cum + len >= arc - 1e-9)
            {
                double t = len < 1e-12 ? 0 : (arc - cum) / len;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double px = a.Easting + t * dx;
                double py = a.Northing + t * dy;
                return loop.Project(new Vec2(px, py));
            }
            cum += len;
        }
        return loop.Project(loop.Polygon[0]);
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
                if (!cells[i].GetCornerKind((CellCorner)c).IsHeadland()) continue;
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
        // Prefer OuterHeadland corners over InnerHeadland — the outer headland
        // ring is the main inter-cell "highway" so entries from it produce
        // shorter, cleaner transits. Fall back to InnerHeadland only when the
        // cell has no outer-side corner. Within the chosen tier, pick the
        // corner closest to the previous exit pose (matches the greedy cell-
        // pick heuristic, so the planner's two phases agree).
        int outerBest = -1, innerBest = -1;
        double outerBestD2 = double.PositiveInfinity, innerBestD2 = double.PositiveInfinity;
        for (int c = 0; c < 4; c++)
        {
            var kind = cell.GetCornerKind((CellCorner)c);
            if (!kind.IsHeadland()) continue;
            var pos = CornerPosition(cell, swaths, (CellCorner)c);
            double dx = pos.Easting - fromPose.Easting;
            double dy = pos.Northing - fromPose.Northing;
            double d2 = dx * dx + dy * dy;
            if (kind == CellCornerKind.OuterHeadland)
            {
                if (d2 < outerBestD2) { outerBestD2 = d2; outerBest = c; }
            }
            else
            {
                if (d2 < innerBestD2) { innerBestD2 = d2; innerBest = c; }
            }
        }
        return outerBest >= 0 ? outerBest : (innerBest >= 0 ? innerBest : 0);
    }

    /// <summary>
    /// Return the (E, N) position of a cell corner in the original frame.
    ///
    /// RotationalSwathGenerator rotates by <c>swathHeading</c> (puts swath
    /// direction along +x); CellCornerClassifier rotates by
    /// <c>sweepHeading = swathHeading + π/2</c> (puts sweep direction along
    /// +x). Working through both transforms with that 90° offset:
    ///   generator.y = −classifier.sweep ⇒ swaths[0] (lowest y in generator)
    ///   sits at the HIGHEST sweep coord; swaths[^1] at the LOWEST.
    ///   generator.x = classifier.perp ⇒ Segments[0] is LowPerp,
    ///   Segments[^1] is HighPerp.
    /// </summary>
    private static Vec2 CornerPosition(Cell cell, List<GeneratedSwath> swaths, CellCorner corner)
    {
        var lowSweepSwath = swaths[^1];
        var highSweepSwath = swaths[0];
        return corner switch
        {
            CellCorner.LowSweepLowPerp => lowSweepSwath.Segments[0].Start,
            CellCorner.LowSweepHighPerp => lowSweepSwath.Segments[^1].End,
            CellCorner.HighSweepLowPerp => highSweepSwath.Segments[0].Start,
            CellCorner.HighSweepHighPerp => highSweepSwath.Segments[^1].End,
            _ => lowSweepSwath.Segments[0].Start,
        };
    }

    private static bool HasAnyHeadlandCorner(Cell cell)
    {
        for (int c = 0; c < 4; c++)
            if (cell.GetCornerKind((CellCorner)c).IsHeadland()) return true;
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
            // Per Höffmann/Hameed: inter-cell transits must ride on the
            // headland-track network so they follow the field/obstacle
            // perimeter rather than cutting through the field. Dubins is
            // used only for the short splices in/out of the network.
            EmitHeadlandRoutedTransit(plan, currentPose, firstEntryPose);
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
                // Intra-cell U-turn at headland: prefer a single-arc semicircle
                // of radius d_perp/2 when geometry allows (clean visual,
                // ~7% longer than Dubins LSL/RSR which gives a flat-bottom U).
                // Falls back to Dubins for non-180° turns or when d_perp/2 < R.
                if (!TryEmitSemicircleUTurn(plan, currentPose, nextEntry))
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

            // Sibling-segment connector: when a swath is split by an inner
            // ring (cell has the pond as a hole), route the connector via the
            // headland-routed transit so it goes AROUND the pond on the inner
            // headland loop rather than cutting straight through. Same
            // machinery as inter-cell transits.
            if (i < orderedSegments.Count - 1)
            {
                var next = orderedSegments[i + 1];
                double nextHeading = HeadingFromTo(next.Start, next.End);
                var fromPose = new Vec3(seg.End.Easting, seg.End.Northing, h);
                var toPose = new Vec3(next.Start.Easting, next.Start.Northing, nextHeading);
                EmitHeadlandRoutedTransit(plan, fromPose, toPose);
                currentPose = plan.Segments[^1].Waypoints.Count > 0
                    ? plan.Segments[^1].Waypoints[^1] : toPose;
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

    // =========================================================================
    // Headland-routed inter-cell transit
    // =========================================================================

    /// <summary>
    /// Inter-cell transit routed via the headland-track network. The route
    /// follows: (1) Dubins splice from the cell-exit pose to the closest
    /// point on the closest headland loop, (2) walk along that loop toward
    /// the next cell's entry projection, (3) if the entry projects to a
    /// different loop, hop with a Dubins jump and walk the second loop,
    /// (4) Dubins splice from the loop back to the next-cell-entry pose.
    ///
    /// For each loop projection, the tangent direction (and corresponding
    /// walk direction) is selected by trying every viable option and picking
    /// the combination that minimizes total path length — this avoids the
    /// "curly Q" anti-pattern where forcing the splice to flip 180° produces
    /// a Dubins LRL/RLR loop near the goal.
    ///
    /// Falls back to direct Dubins when no loops are configured.
    /// </summary>
    private void EmitHeadlandRoutedTransit(RoutePlan plan, Vec3 from, Vec3 to)
    {
        if (_headlandLoops.Count == 0)
        {
            EmitDubinsTransit(plan, from, to);
            return;
        }

        var fromPos = new Vec2(from.Easting, from.Northing);
        var toPos = new Vec2(to.Easting, to.Northing);
        var fromLoop = ClosestLoop(fromPos, out var fromProj);
        var toLoop = ClosestLoop(toPos, out var toProj);
        if (fromLoop == null || toLoop == null)
        {
            EmitDubinsTransit(plan, from, to);
            return;
        }

        TransitCandidate? best = null;
        if (fromLoop == toLoop)
        {
            foreach (bool forward in new[] { true, false })
            {
                var c = BuildSameLoopCandidate(from, to, fromLoop, fromProj, toProj, forward);
                if (best == null || c.TotalLength < best.Value.TotalLength) best = c;
            }
        }
        else
        {
            var (fromBridge, toBridge) = NearestPointsBetweenLoops(fromLoop, toLoop);
            foreach (bool fOnFrom in new[] { true, false })
                foreach (bool fOnTo in new[] { true, false })
                {
                    var c = BuildCrossLoopCandidate(from, to, fromLoop, fromProj, toLoop, toProj,
                        fromBridge, toBridge, fOnFrom, fOnTo);
                    if (best == null || c.TotalLength < best.Value.TotalLength) best = c;
                }
        }

        if (best == null)
        {
            EmitDubinsTransit(plan, from, to);
            return;
        }

        plan.Segments.Add(new RouteSegment
        {
            Type = RouteSegmentType.Transit,
            Waypoints = best.Value.Waypoints,
            Length = best.Value.TotalLength,
            IsTurnValid = best.Value.AllSplicesSane,
            TurnPathType = "HeadlandRouted",
        });
    }

    private struct TransitCandidate
    {
        public List<Vec3> Waypoints;
        public double TotalLength;
        public bool AllSplicesSane;
    }

    private TransitCandidate BuildSameLoopCandidate(
        Vec3 from, Vec3 to, HeadlandLoop loop, LoopProjection fromProj, LoopProjection toProj, bool forward)
    {
        // Align both sides so the splices come in/out tangent-matched. Out-side
        // searches forward (in walk direction) past toProj; in-side searches
        // forward in walk direction past fromProj — i.e. we may enter the loop
        // SLIGHTLY LATER than the perpendicular projection so the entry pose's
        // tangent matches `from`'s heading. Each side's search is bounded by
        // the other's projection so the walk doesn't end up going backward.
        var alignedToProj = AlignTangentToHeading(loop, toProj, to.Heading, forward,
            searchForward: true, boundary: fromProj);
        var alignedFromProj = AlignTangentToHeading(loop, fromProj, from.Heading, forward,
            searchForward: true, boundary: alignedToProj);

        var waypoints = new List<Vec3>();
        double total = 0;
        bool sane = true;

        var inPose = new Vec3(alignedFromProj.Position.Easting, alignedFromProj.Position.Northing,
            HeadingFromTangent(alignedFromProj.Tangent, forward));
        total += AppendDubinsSpliceTracked(waypoints, from, inPose, out bool s1);
        sane &= s1;

        var walk = loop.Walk(alignedFromProj, alignedToProj, forward, maxStep: 1.0);
        AppendWaypoints(waypoints, walk);
        total += forward
            ? loop.ForwardArc(alignedFromProj, alignedToProj)
            : (loop.Perimeter - loop.ForwardArc(alignedFromProj, alignedToProj));

        var outPose = new Vec3(alignedToProj.Position.Easting, alignedToProj.Position.Northing,
            HeadingFromTangent(alignedToProj.Tangent, forward));
        total += AppendDubinsSpliceTracked(waypoints, outPose, to, out bool s2);
        sane &= s2;

        return new TransitCandidate { Waypoints = waypoints, TotalLength = total, AllSplicesSane = sane };
    }

    /// <summary>
    /// Find a loop point whose tangent (in <paramref name="forward"/> walk
    /// direction) matches <paramref name="targetHeading"/> within 90°, by
    /// searching from <paramref name="anchor"/> in the chosen direction.
    /// <paramref name="searchForward"/> = true searches in the walk direction
    /// (use when we want to consume some loop walk before exiting); false
    /// searches against the walk direction (use when we want to enter the
    /// loop earlier than the projection).
    ///
    /// <paramref name="boundary"/> is a point we mustn't cross — for the
    /// out-side it's the in-side projection (walking past would mean walking
    /// almost the whole loop); for the in-side it's the out-side projection.
    /// Returns the original <paramref name="anchor"/> if already aligned
    /// within 90° or if no better point exists.
    ///
    /// Inner loops are NOT walk-past'd: those loops surround obstacles, and
    /// the chord from a splice partner (some pose off the loop) to a walked-
    /// around tangent-aligned point can cut through the obstacle interior.
    /// On inner loops we accept the residual curl rather than route through
    /// the pond.
    /// </summary>
    private static LoopProjection AlignTangentToHeading(
        HeadlandLoop loop, LoopProjection anchor, double targetHeading, bool forward,
        bool searchForward, LoopProjection? boundary = null)
    {
        if (loop.Kind == HeadlandLoopKind.Inner) return anchor;

        double currentTangentHeading = HeadingFromTangent(anchor.Tangent, forward);
        if (HeadingMismatchRad(currentTangentHeading, targetHeading) <= Math.PI / 2)
            return anchor;

        double startArc = anchor.ArcLength;
        // ¼ perimeter cap: enough to find the next 90° of tangent rotation in
        // either direction, but bounded so the walk-past can't wander far
        // enough around an obstacle-shaped loop to put the splice through the
        // obstacle. If a tangent match isn't found inside the cap, the
        // original anchor is returned (caller absorbs the residual curl).
        double maxExtra = loop.Perimeter * 0.25;
        double step = Math.Max(0.5, loop.Perimeter / 200.0);
        LoopProjection best = anchor;
        double bestMismatch = HeadingMismatchRad(currentTangentHeading, targetHeading);
        // "search forward" = same sign as walk direction; "search backward" = opposite.
        int sign = (searchForward == forward) ? +1 : -1;

        for (double extra = step; extra <= maxExtra; extra += step)
        {
            double sampleArc = (startArc + sign * extra + loop.Perimeter * 10) % loop.Perimeter;
            if (boundary.HasValue && Math.Abs(sampleArc - boundary.Value.ArcLength) < step) break;

            var sample = ProjectionAtArcLength(loop, sampleArc);
            double tangentHeading = HeadingFromTangent(sample.Tangent, forward);
            double mismatch = HeadingMismatchRad(tangentHeading, targetHeading);
            if (mismatch < bestMismatch)
            {
                bestMismatch = mismatch;
                best = sample;
                if (mismatch < 0.1) break;
            }
        }
        return best;
    }

    private static double HeadingMismatchRad(double a, double b)
    {
        const double TwoPi = 2.0 * Math.PI;
        double d = ((a - b) % TwoPi + TwoPi) % TwoPi;
        if (d > Math.PI) d = TwoPi - d;
        return d;
    }

    private TransitCandidate BuildCrossLoopCandidate(
        Vec3 from, Vec3 to,
        HeadlandLoop fromLoop, LoopProjection fromProj,
        HeadlandLoop toLoop, LoopProjection toProj,
        LoopProjection fromBridge, LoopProjection toBridge,
        bool forwardOnFrom, bool forwardOnTo)
    {
        // Tangent-align the OUT-side projection (from cell-exit's perspective
        // it's the entry to the to-loop). Don't apply walk-past on the IN
        // side here — for inner loops around obstacles, walking the entry
        // point past the perpendicular projection can route the splice
        // through the obstacle (the chord from cell-exit to a walked-around
        // entry on a small loop crosses the loop's interior).
        var alignedToProj = AlignTangentToHeading(toLoop, toProj, to.Heading, forwardOnTo,
            searchForward: true, boundary: toBridge);

        var waypoints = new List<Vec3>();
        double total = 0;
        bool sane = true;

        var inPose = new Vec3(fromProj.Position.Easting, fromProj.Position.Northing,
            HeadingFromTangent(fromProj.Tangent, forwardOnFrom));
        total += AppendDubinsSpliceTracked(waypoints, from, inPose, out bool s1);
        sane &= s1;

        var walkA = fromLoop.Walk(fromProj, fromBridge, forwardOnFrom, maxStep: 1.0);
        AppendWaypoints(waypoints, walkA);
        total += forwardOnFrom
            ? fromLoop.ForwardArc(fromProj, fromBridge)
            : (fromLoop.Perimeter - fromLoop.ForwardArc(fromProj, fromBridge));

        var bridgeStart = new Vec3(fromBridge.Position.Easting, fromBridge.Position.Northing,
            HeadingFromTangent(fromBridge.Tangent, forwardOnFrom));
        var bridgeEnd = new Vec3(toBridge.Position.Easting, toBridge.Position.Northing,
            HeadingFromTangent(toBridge.Tangent, forwardOnTo));
        total += AppendDubinsSpliceTracked(waypoints, bridgeStart, bridgeEnd, out bool s2);
        sane &= s2;

        var walkB = toLoop.Walk(toBridge, alignedToProj, forwardOnTo, maxStep: 1.0);
        AppendWaypoints(waypoints, walkB);
        total += forwardOnTo
            ? toLoop.ForwardArc(toBridge, alignedToProj)
            : (toLoop.Perimeter - toLoop.ForwardArc(toBridge, alignedToProj));

        var outPose = new Vec3(alignedToProj.Position.Easting, alignedToProj.Position.Northing,
            HeadingFromTangent(alignedToProj.Tangent, forwardOnTo));
        total += AppendDubinsSpliceTracked(waypoints, outPose, to, out bool s3);
        sane &= s3;

        return new TransitCandidate { Waypoints = waypoints, TotalLength = total, AllSplicesSane = sane };
    }

    /// <summary>Loop with the smallest projection distance to the query point.</summary>
    private HeadlandLoop? ClosestLoop(Vec2 point, out LoopProjection bestProj)
    {
        HeadlandLoop? best = null;
        bestProj = default;
        double bestD2 = double.PositiveInfinity;
        foreach (var loop in _headlandLoops)
        {
            var proj = loop.Project(point);
            double dx = proj.Position.Easting - point.Easting;
            double dy = proj.Position.Northing - point.Northing;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = loop; bestProj = proj; }
        }
        return best;
    }

    /// <summary>True when forward (CCW) walk from a→b is shorter than backward.</summary>
    private static bool ForwardIsShorter(HeadlandLoop loop, LoopProjection a, LoopProjection b)
    {
        double fwd = loop.ForwardArc(a, b);
        return fwd <= loop.Perimeter - fwd;
    }

    /// <summary>
    /// Tangent-direction hint for the loop-entry splice pose. For same-loop
    /// trips, point in whichever direction gives the shorter walk to the
    /// destination. For cross-loop trips, point toward the destination loop
    /// (rough heuristic — the Dubins splice handles any mismatch).
    /// </summary>
    private static bool PreferredForwardOnLoop(HeadlandLoop fromLoop, LoopProjection fromProj,
        HeadlandLoop toLoop, LoopProjection toProj)
    {
        if (fromLoop == toLoop) return ForwardIsShorter(fromLoop, fromProj, toProj);
        var c = LoopCentroid(toLoop);
        double dx = c.Easting - fromProj.Position.Easting;
        double dy = c.Northing - fromProj.Position.Northing;
        return (dx * fromProj.Tangent.Easting + dy * fromProj.Tangent.Northing) >= 0;
    }

    /// <summary>Find the nearest pair of points between two loops (one projection on each).</summary>
    private static (LoopProjection onA, LoopProjection onB) NearestPointsBetweenLoops(HeadlandLoop a, HeadlandLoop b)
    {
        // O(n*m) but loops are small (~tens of vertices each).
        LoopProjection bestA = default, bestB = default;
        double bestD2 = double.PositiveInfinity;
        foreach (var pa in a.Polygon)
        {
            var pbProj = b.Project(pa);
            double dx = pbProj.Position.Easting - pa.Easting;
            double dy = pbProj.Position.Northing - pa.Northing;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestA = a.Project(pa);   // exact projection lands on a vertex => on-perimeter
                bestB = pbProj;
            }
        }
        return (bestA, bestB);
    }

    private static Vec2 LoopCentroid(HeadlandLoop loop)
    {
        double cx = 0, cy = 0;
        foreach (var p in loop.Polygon) { cx += p.Easting; cy += p.Northing; }
        int n = loop.Polygon.Count;
        return n == 0 ? new Vec2(0, 0) : new Vec2(cx / n, cy / n);
    }

    private static void AppendWaypoints(List<Vec3> sink, List<Vec3> add)
    {
        // Skip the first sample if it's a near-duplicate of the last in sink.
        int start = 0;
        if (sink.Count > 0 && add.Count > 0)
        {
            var s = sink[^1];
            var a = add[0];
            double dx = s.Easting - a.Easting;
            double dy = s.Northing - a.Northing;
            if (dx * dx + dy * dy < 1e-6) start = 1;
        }
        for (int i = start; i < add.Count; i++) sink.Add(add[i]);
    }

    /// <summary>
    /// Append a Dubins-path splice between two poses to the waypoint sink and
    /// return its length. Falls back to a straight line if Dubins finds no
    /// solution (very short splices). Wrapper without sanity tracking.
    /// </summary>
    private double AppendDubinsSplice(List<Vec3> sink, Vec3 from, Vec3 to)
        => AppendDubinsSpliceTracked(sink, from, to, out _);

    /// <summary>
    /// Append a Dubins-path splice and report whether the result is "sane" —
    /// that is, the path length isn't absurdly larger than the straight-line
    /// chord. Insane results indicate Dubins picked an LRL/RLR family to flip
    /// a near-180° heading mismatch (the "curly Q" pattern), and the caller
    /// can use the flag to mark the surrounding segment IsTurnValid=false or
    /// to prefer an alternative configuration.
    /// </summary>
    private double AppendDubinsSpliceTracked(List<Vec3> sink, Vec3 from, Vec3 to, out bool sane)
    {
        double dx = to.Easting - from.Easting;
        double dy = to.Northing - from.Northing;
        double chord = Math.Sqrt(dx * dx + dy * dy);

        var pd = _dubins.GetBestPathData(from, to);
        if (pd == null || pd.PathCoordinates == null || pd.PathCoordinates.Count < 2)
        {
            // Straight line fallback for trivially-close poses or no-solution cases.
            if (sink.Count == 0 || !PoseNearlyEqual(sink[^1], from)) sink.Add(from);
            sink.Add(to);
            sane = chord < _turningRadius; // straight-line OK only for very short distances
            return chord;
        }

        // "Sane" = path doesn't blow past 5× chord (or 5× R for tiny chords).
        // 5× is generous — a tight Dubins for a 90° heading flip is ~2.5×
        // chord; LRL/RLR loops typically run 6–10× chord.
        double maxSane = Math.Max(5.0 * chord, 5.0 * _turningRadius);
        sane = pd.TotalLength <= maxSane;

        var coords = pd.PathCoordinates;
        var pts = new List<Vec3>(coords.Count);
        for (int i = 0; i < coords.Count - 1; i++)
        {
            double cdE = coords[i + 1].Easting - coords[i].Easting;
            double cdN = coords[i + 1].Northing - coords[i].Northing;
            double h = (Math.Abs(cdE) < 1e-12 && Math.Abs(cdN) < 1e-12)
                ? (pts.Count > 0 ? pts[^1].Heading : from.Heading)
                : Math.Atan2(cdE, cdN);
            pts.Add(new Vec3(coords[i].Easting, coords[i].Northing, NormalizeHeading(h)));
        }
        pts.Add(new Vec3(to.Easting, to.Northing, to.Heading));
        AppendWaypoints(sink, pts);
        return pd.TotalLength;
    }

    private static bool PoseNearlyEqual(Vec3 a, Vec3 b)
    {
        double dx = a.Easting - b.Easting;
        double dy = a.Northing - b.Northing;
        return dx * dx + dy * dy < 1e-6;
    }

    private static double HeadingFromTangent(Vec2 tangent, bool forward)
    {
        double dE = forward ? tangent.Easting : -tangent.Easting;
        double dN = forward ? tangent.Northing : -tangent.Northing;
        double h = Math.Atan2(dE, dN);
        const double TwoPi = 2.0 * Math.PI;
        if (h < 0) h += TwoPi;
        return h;
    }

    /// <summary>
    /// Try to emit a clean single-arc U-turn from <paramref name="from"/> to
    /// <paramref name="to"/>. Works for any pair of antiparallel poses (heading
    /// flipped 180°) where the unique forward arc has a radius at least the
    /// vehicle's minimum turning radius — that includes pure semicircles and
    /// (when the chord isn't perpendicular to from's heading) slightly larger
    /// arcs sweeping a bit more than π. Returns false when geometry doesn't
    /// fit; caller falls back to Dubins.
    ///
    /// Why bother when Dubins LSL/RSR already finds a valid forward path: for
    /// R &lt; d_perp/2 it picks an arc-straight-arc shape with a visible flat
    /// bottom, geometrically optimal but visually a "lump." The agricultural
    /// convention is a single arc; the vehicle can drive arcs wider than its
    /// minimum turning radius without trouble. ~7% longer than Dubins shortest.
    /// </summary>
    private bool TryEmitSemicircleUTurn(RoutePlan plan, Vec3 from, Vec3 to)
    {
        const double TwoPi = 2.0 * Math.PI;

        // Heading must be flipped within ~6° of 180°.
        double headingDelta = ((to.Heading - from.Heading - Math.PI) % TwoPi + TwoPi + Math.PI) % TwoPi - Math.PI;
        if (Math.Abs(headingDelta) > 0.1) return false;

        // Vector from→to, decomposed along/perp to from's heading.
        double dE = to.Easting - from.Easting;
        double dN = to.Northing - from.Northing;
        double sinH = Math.Sin(from.Heading);
        double cosH = Math.Cos(from.Heading);
        double perpDot = dE * cosH - dN * sinH;          // right of from (positive = right)
        double alongDot = dE * sinH + dN * cosH;          // forward of from
        double chordSq = dE * dE + dN * dN;
        if (chordSq < 1e-4) return false;
        if (Math.Abs(perpDot) < 1e-3) return false;     // chord parallel to heading — not a U-turn

        // A single circle is tangent to BOTH antiparallel poses ONLY when the
        // chord is perpendicular to the heading (alongDot = 0). When alongDot
        // ≠ 0 the formula r = chord²/(2·perpDot) produces a circle tangent at
        // `from` but NOT at `to` — the exit heading would be off by up to
        // arctan(alongDot / (perpDot − r)), visible as an abrupt corner where
        // the arc meets the next swath. For along-offset > a fraction of the
        // perp-offset, fall back to Dubins (which always tangents correctly
        // at both ends with an arc-straight-arc pattern).
        if (Math.Abs(alongDot) > 0.1 * Math.Abs(perpDot)) return false;

        // Single-arc U-turn: center sits on the line through `from`
        // perpendicular to its heading, AND on the perpendicular bisector of
        // chord(from, to). Solving: r = |chord|² / (2·perpDot). Sign of
        // perpDot picks the turn direction.
        double radius = Math.Abs(chordSq / (2.0 * perpDot));
        if (radius < _turningRadius - 1e-6) return false;

        // Sanity: reject absurd radii. A real intra-cell U-turn has r ≈
        // chord/2 (perpendicular antiparallel poses); r >> chord means the
        // chord is mostly parallel to the heading — i.e. the two swath
        // endpoints aren't actually a U-turn pair (likely a corner-pick or
        // ordering bug upstream). Falling back to Dubins won't help geometry-
        // wise, but at least the renderer won't draw an arc the size of a
        // small county.
        double chord = Math.Sqrt(chordSq);
        if (radius > 5.0 * chord) return false;

        // Center = from + r · perp_toward_to. perpDot > 0 means `to` is on the
        // RIGHT of from's heading → vehicle turns CW, center to the right.
        // perpDot < 0 → CCW, center to the left.
        double signedR = perpDot > 0 ? radius : -radius;
        double cx = from.Easting + signedR * cosH;
        double cy = from.Northing - signedR * sinH;

        // Turn direction: turnSign = +1 for CCW, -1 for CW.
        int turnSign = perpDot > 0 ? -1 : +1;

        // Sweep angle from `from` to `to` around center. Going from
        // start_angle to goal_angle in turnSign direction. For perpDot > 0
        // (CW), sweep is negative when measured as Δangle.
        double startAngle = Math.Atan2(from.Northing - cy, from.Easting - cx);
        double goalAngle = Math.Atan2(to.Northing - cy, to.Easting - cx);
        double sweep = goalAngle - startAngle;
        // Normalize to the chosen direction. CCW (+1): sweep ∈ (0, 2π).
        // CW (-1): sweep ∈ (-2π, 0).
        if (turnSign > 0)
        {
            while (sweep <= 0) sweep += TwoPi;
            while (sweep > TwoPi) sweep -= TwoPi;
        }
        else
        {
            while (sweep >= 0) sweep -= TwoPi;
            while (sweep < -TwoPi) sweep += TwoPi;
        }

        // Sample the arc at DriveDistance spacing.
        const double driveDistance = 0.05;
        double arcLength = Math.Abs(sweep) * radius;
        int steps = Math.Max(8, (int)Math.Ceiling(arcLength / driveDistance));

        var waypoints = new List<Vec3>(steps + 1);
        for (int i = 0; i < steps; i++)
        {
            double t = (double)i / steps;
            double a = startAngle + sweep * t;
            double x = cx + radius * Math.Cos(a);
            double y = cy + radius * Math.Sin(a);
            // Tangent direction (E, N) = (turnSign · −sin a, turnSign · cos a).
            double tE = -turnSign * Math.Sin(a);
            double tN = turnSign * Math.Cos(a);
            waypoints.Add(new Vec3(x, y, NormalizeHeading(Math.Atan2(tE, tN))));
        }
        // Snap last waypoint exactly to goal — the arc lands on goal by
        // construction, but floating-point may leave a sub-mm offset.
        waypoints.Add(new Vec3(to.Easting, to.Northing, to.Heading));

        plan.Segments.Add(new RouteSegment
        {
            Type = RouteSegmentType.Turn,
            Waypoints = waypoints,
            Length = arcLength,
            IsTurnValid = true,
            TurnPathType = "SingleArc",
        });
        return true;
    }

    /// <summary>
    /// Forward-only Dubins turn for intra-cell U-turns. Sampled at full
    /// <see cref="DubinsPathService.DriveDistance"/> resolution so the
    /// rendered polyline reads as a smooth arc rather than the 0.25m-step
    /// polygonal lumps GenerateAllPaths produces. Falls back to Reeds-Shepp
    /// when Dubins has no forward path at all. If Dubins picks an LRL/RLR
    /// loop (path length &gt; 5× chord, the "around the world" pattern), emits
    /// a straight-line segment marked IsTurnValid=false instead so the user
    /// sees a clearly-broken connection rather than a silently-wrong loop.
    /// </summary>
    private void EmitDubinsTurn(RoutePlan plan, Vec3 from, Vec3 to)
    {
        double dx = to.Easting - from.Easting;
        double dy = to.Northing - from.Northing;
        double chord = Math.Sqrt(dx * dx + dy * dy);

        var pd = _dubins.GetBestPathData(from, to);
        if (pd == null || pd.PathCoordinates == null || pd.PathCoordinates.Count < 2)
        {
            // No forward path — fall back to Reeds-Shepp 3-point turn.
            EmitReedsSheppTransit(plan, from, to, RouteSegmentType.Turn);
            return;
        }

        // Sanity guard: an LRL/RLR loop has path length ≫ chord. For an
        // intra-cell U-turn (consecutive swaths of width opWidth) we expect
        // roughly chord/2 to chord-and-a-bit length. >5× chord means Dubins
        // picked a loop family because the heading mismatch isn't a real
        // U-turn — the upstream swath ordering is wrong. Surface the bug
        // visibly instead of drawing a loop the size of the field.
        double maxSane = Math.Max(5.0 * chord, 5.0 * _turningRadius);
        if (pd.TotalLength > maxSane)
        {
            plan.Segments.Add(new RouteSegment
            {
                Type = RouteSegmentType.Turn,
                Waypoints = new List<Vec3>
                {
                    new(from.Easting, from.Northing, from.Heading),
                    new(to.Easting,   to.Northing,   to.Heading),
                },
                Length = chord,
                IsTurnValid = false,
                TurnPathType = "InvalidLoop",
            });
            return;
        }

        // Emit every raw Vec2 (DriveDistance ≈ 5cm spacing). Heading at each
        // sample = direction to next sample; last sample inherits the previous
        // heading. Snap the final waypoint to the exact goal so the chain stays
        // continuous (DubinsPathService floors segment lengths and may overshoot
        // by up to one DriveDistance step).
        var coords = pd.PathCoordinates;
        var waypoints = new List<Vec3>(coords.Count);
        for (int i = 0; i < coords.Count - 1; i++)
        {
            double dE = coords[i + 1].Easting - coords[i].Easting;
            double dN = coords[i + 1].Northing - coords[i].Northing;
            double h = (Math.Abs(dE) < 1e-12 && Math.Abs(dN) < 1e-12)
                ? (waypoints.Count > 0 ? waypoints[^1].Heading : from.Heading)
                : Math.Atan2(dE, dN);
            waypoints.Add(new Vec3(coords[i].Easting, coords[i].Northing, NormalizeHeading(h)));
        }
        waypoints.Add(new Vec3(to.Easting, to.Northing, to.Heading));

        plan.Segments.Add(new RouteSegment
        {
            Type = RouteSegmentType.Turn,
            Waypoints = waypoints,
            Length = pd.TotalLength,
            IsTurnValid = true,
            TurnPathType = pd.PathType.ToString(),
        });
    }

    /// <summary>
    /// Forward-only Dubins inter-cell transit. Same density/snap treatment as
    /// <see cref="EmitDubinsTurn"/> but tagged Transit so downstream code (and
    /// rendering) treats it as non-coverage travel. Falls back to Reeds-Shepp
    /// only when Dubins finds no forward solution.
    /// </summary>
    private void EmitDubinsTransit(RoutePlan plan, Vec3 from, Vec3 to)
    {
        var pd = _dubins.GetBestPathData(from, to);
        if (pd == null || pd.PathCoordinates == null || pd.PathCoordinates.Count < 2)
        {
            EmitReedsSheppTransit(plan, from, to, RouteSegmentType.Transit);
            return;
        }

        var coords = pd.PathCoordinates;
        var waypoints = new List<Vec3>(coords.Count);
        for (int i = 0; i < coords.Count - 1; i++)
        {
            double dE = coords[i + 1].Easting - coords[i].Easting;
            double dN = coords[i + 1].Northing - coords[i].Northing;
            double h = (Math.Abs(dE) < 1e-12 && Math.Abs(dN) < 1e-12)
                ? (waypoints.Count > 0 ? waypoints[^1].Heading : from.Heading)
                : Math.Atan2(dE, dN);
            waypoints.Add(new Vec3(coords[i].Easting, coords[i].Northing, NormalizeHeading(h)));
        }
        waypoints.Add(new Vec3(to.Easting, to.Northing, to.Heading));

        plan.Segments.Add(new RouteSegment
        {
            Type = RouteSegmentType.Transit,
            Waypoints = waypoints,
            Length = pd.TotalLength,
            IsTurnValid = true,
            TurnPathType = pd.PathType.ToString(),
        });
    }

    private static double NormalizeHeading(double h)
    {
        const double TwoPi = 2.0 * Math.PI;
        h = h % TwoPi;
        if (h < 0) h += TwoPi;
        return h;
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
