// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Assembles ordered swaths and validated turn paths into a RoutePlan.
///
/// For each swath pair the stitcher chooses one of three connection types:
/// 1. <b>Transit</b> when the next swath is a split sibling (same source index) —
///    the next piece is on the other side of an obstacle, so a Dubins turn is
///    nonsensical; trace along a perimeter circuit instead.
/// 2. <b>Turn</b> (Dubins) when the next swath is in the same zone — the existing
///    boundary-validated turn.
/// 3. <b>Transit</b> as a fallback when the Dubins turn fails validation —
///    the swaths are in different zones (e.g. concave outer notch, non-convex
///    headland neck). Trace along the best available circuit.
/// </summary>
public class RouteStitchingService : IRouteStitchingService
{
    private readonly ITurnPathService _turnPathService;
    private readonly ITransitPathService _transitPathService;

    public RouteStitchingService(ITurnPathService turnPathService, ITransitPathService transitPathService)
    {
        _turnPathService = turnPathService;
        _transitPathService = transitPathService;
    }

    public RoutePlan StitchRoute(List<Models.Track.Track> swaths, RouteStitchConfig config,
        List<int>? sourceSwathIndex = null)
    {
        var plan = new RoutePlan
        {
            Pattern = config.Pattern.ToString(),
        };

        if (swaths.Count == 0)
            return plan;

        // Reorder split pieces of each source so they appear in traversal order.
        // SwathGenerationService outputs pieces in geometric order along the
        // heading (low-t to high-t); for a source driven in reverse, traversal
        // order is the opposite. Reorder up-front so the rest of the loop can
        // assume swaths[i] precedes swaths[i+1] in actual travel order.
        if (sourceSwathIndex != null)
            (swaths, sourceSwathIndex) = ReorderSplitPiecesForTraversal(swaths, sourceSwathIndex);

        double heading = config.ReferenceHeading;

        for (int i = 0; i < swaths.Count; i++)
        {
            var swath = swaths[i];
            if (swath.Points.Count < 2) continue;

            // Travel direction is per-source, not per-list-position. With splits,
            // multiple list entries share a source, and they must all be driven
            // in the same direction. Without sourceSwathIndex (legacy callers),
            // fall back to the old per-position alternation.
            bool isReverse = sourceSwathIndex != null && i < sourceSwathIndex.Count
                ? sourceSwathIndex[i] % 2 != 0
                : i % 2 != 0;
            var waypoints = new List<Vec3>(swath.Points);

            // Calculate swath length
            double swathLength = 0;
            for (int j = 1; j < waypoints.Count; j++)
            {
                double dx = waypoints[j].Easting - waypoints[j - 1].Easting;
                double dy = waypoints[j].Northing - waypoints[j - 1].Northing;
                swathLength += Math.Sqrt(dx * dx + dy * dy);
            }

            plan.Segments.Add(new RouteSegment
            {
                Type = RouteSegmentType.Swath,
                SwathIndex = i,
                Waypoints = waypoints,
                Length = swathLength,
                IsReverse = isReverse,
                IsTurnValid = true,
            });

            // Generate connection to next swath (if not the last swath).
            if (i >= swaths.Count - 1) continue;
            var next = swaths[i + 1];
            if (next.Points.Count < 2) continue;

            // Direction of next swath (same source = same direction; new source = alternating).
            bool isReverseNext = sourceSwathIndex != null && i + 1 < sourceSwathIndex.Count
                ? sourceSwathIndex[i + 1] % 2 != 0
                : (i + 1) % 2 != 0;

            // Exit from current swath at the end of the current direction.
            Vec3 exitPoint = isReverse ? swath.Points[0] : swath.Points[^1];
            double exitHeading = isReverse ? heading + Math.PI : heading;

            // Enter next swath at the start of the next direction.
            Vec3 entryPoint = isReverseNext ? next.Points[^1] : next.Points[0];
            double entryHeading = isReverseNext ? heading + Math.PI : heading;

            // Generate Dubins turn unconditionally; it's the "no obstacle" answer.
            var turnInput = new TurnPathInput
            {
                ExitPoint = exitPoint,
                ExitHeading = exitHeading,
                EntryPoint = entryPoint,
                EntryHeading = entryHeading,
                TurningRadius = config.TurningRadius,
                HeadlandWidth = config.HeadlandWidth,
                Boundary = config.Boundary,
                InnerBoundaries = config.InnerBoundaries,
            };
            var turnResult = _turnPathService.GenerateTurn(turnInput);

            // If Dubins is valid, prefer it (shorter / direct).
            // Otherwise — split sibling, concave outer, or non-convex headland —
            // try a transit along the available perimeter circuits.
            RouteSegment connection;
            bool needTransit = !turnResult.IsValid && config.Circuits.Count > 0;
            TransitPathResult? transitResult = needTransit
                ? _transitPathService.GenerateTransit(new TransitPathInput
                {
                    ExitPoint = exitPoint,
                    ExitHeading = exitHeading,
                    EntryPoint = entryPoint,
                    EntryHeading = entryHeading,
                    TurningRadius = config.TurningRadius,
                    OuterBoundary = config.Boundary,
                    InnerBoundaries = config.RawInnerBoundaries,
                    Circuits = config.Circuits,
                })
                : null;

            if (turnResult.IsValid)
            {
                connection = MakeTurn(turnResult, i);
            }
            else if (transitResult != null && transitResult.IsValid)
            {
                connection = MakeTransit(transitResult, i);
            }
            else if (transitResult != null && transitResult.Waypoints.Count > 0)
            {
                // Both attempts invalid; transit reached a circuit but couldn't fully
                // route inside outer/outside inner. Render the transit attempt — its
                // shape at least hints at the routing path the planner tried.
                connection = MakeTransit(transitResult, i);
            }
            else
            {
                // No circuits, or transit produced nothing. Keep Dubins best-effort.
                connection = MakeTurn(turnResult, i);
            }

            plan.Segments.Add(connection);
        }

        return plan;
    }

    /// <summary>
    /// Reorder split pieces of each source so they appear in traversal order.
    /// SwathGenerationService outputs pieces in geometric order along the heading;
    /// for a source driven in reverse (odd source index), traversal order is the
    /// reverse of geometric order. Sources with a single piece are unaffected.
    /// </summary>
    private static (List<Models.Track.Track>, List<int>) ReorderSplitPiecesForTraversal(
        List<Models.Track.Track> swaths, List<int> sourceSwathIndex)
    {
        if (swaths.Count != sourceSwathIndex.Count || swaths.Count <= 1)
            return (swaths, sourceSwathIndex);

        var outSwaths = new List<Models.Track.Track>(swaths.Count);
        var outSources = new List<int>(sourceSwathIndex.Count);

        int i = 0;
        while (i < swaths.Count)
        {
            int src = sourceSwathIndex[i];
            int j = i;
            while (j < swaths.Count && sourceSwathIndex[j] == src) j++;
            // [i, j) is one source's group. Reverse if source is reverse-direction.
            bool reverseGroup = (src % 2) != 0;
            if (reverseGroup && j - i > 1)
            {
                for (int k = j - 1; k >= i; k--)
                {
                    outSwaths.Add(swaths[k]);
                    outSources.Add(src);
                }
            }
            else
            {
                for (int k = i; k < j; k++)
                {
                    outSwaths.Add(swaths[k]);
                    outSources.Add(src);
                }
            }
            i = j;
        }

        return (outSwaths, outSources);
    }

    /// <summary>
    /// Cell-aware stitching: walks a sequence of <see cref="CellTraversalPlanner.CellVisit"/>s,
    /// emitting per-swath segments with Dubins turns inside each cell and Dubins transits
    /// between cells. Replaces the legacy <see cref="StitchRoute"/> path when BCD is in use.
    /// </summary>
    public RoutePlan StitchFromCells(
        List<CellTraversalPlanner.CellVisit> visits,
        Models.BoundaryPolygon outerBoundary,
        List<Models.BoundaryPolygon> innerBoundaries,
        double turningRadius,
        double headlandWidth)
    {
        var plan = new RoutePlan { Pattern = "BCD" };
        if (visits.Count == 0) return plan;

        Vec3? prevExitPose = null;
        int globalSwathIndex = 0;

        for (int v = 0; v < visits.Count; v++)
        {
            var visit = visits[v];
            var swaths = visit.SwathsInTraversalOrder;
            int n = swaths.Count;
            if (n == 0) continue;

            bool startReversed = (visit.EntryCorner == 1 || visit.EntryCorner == 3);

            // Inter-cell transit (skip for the first visit).
            if (prevExitPose is { } prevExit)
            {
                var firstSwath = swaths[0];
                bool firstReversed = startReversed;
                Vec3 entryPose = firstReversed ? firstSwath.Points[^1] : firstSwath.Points[0];
                double entryHeading = firstReversed
                    ? visit.EntryHeading + Math.PI
                    : visit.EntryHeading;
                // EntryHeading on the visit is set so that 0/2 corners drive to higher
                // perp and 1/3 to lower; we want the heading at entryPose.
                entryHeading = visit.EntryHeading;
                var entryPoint = new Vec3(entryPose.Easting, entryPose.Northing, entryHeading);

                var turn = _turnPathService.GenerateTurn(new TurnPathInput
                {
                    ExitPoint = prevExit,
                    ExitHeading = prevExit.Heading,
                    EntryPoint = entryPoint,
                    EntryHeading = entryHeading,
                    TurningRadius = turningRadius,
                    HeadlandWidth = headlandWidth,
                    Boundary = outerBoundary,
                    InnerBoundaries = innerBoundaries,
                });

                plan.Segments.Add(new RouteSegment
                {
                    Type = RouteSegmentType.Transit,
                    SwathIndex = globalSwathIndex - 1,
                    Waypoints = turn.Waypoints,
                    Length = turn.Length,
                    IsTurnValid = turn.IsValid,
                    TurnPathType = $"transit:cell{visits[v - 1].CellId}->cell{visit.CellId}",
                });
            }

            for (int i = 0; i < n; i++)
            {
                var swath = swaths[i];
                if (swath.Points.Count < 2) continue;

                bool isReverse = startReversed ^ ((i & 1) == 1);

                var waypoints = new List<Vec3>(swath.Points);
                double swathLen = 0;
                for (int j = 1; j < waypoints.Count; j++)
                {
                    double dx = waypoints[j].Easting - waypoints[j - 1].Easting;
                    double dy = waypoints[j].Northing - waypoints[j - 1].Northing;
                    swathLen += Math.Sqrt(dx * dx + dy * dy);
                }

                plan.Segments.Add(new RouteSegment
                {
                    Type = RouteSegmentType.Swath,
                    SwathIndex = globalSwathIndex,
                    Waypoints = waypoints,
                    Length = swathLen,
                    IsReverse = isReverse,
                    IsTurnValid = true,
                });
                globalSwathIndex++;

                Vec3 swathExit = isReverse ? swath.Points[0] : swath.Points[^1];
                double swathExitHeading = isReverse
                    ? waypoints[0].Heading + Math.PI
                    : waypoints[^1].Heading;
                var exitPose = new Vec3(swathExit.Easting, swathExit.Northing, swathExitHeading);

                if (i < n - 1)
                {
                    // Intra-cell boustrophedon turn to the next swath.
                    var next = swaths[i + 1];
                    if (next.Points.Count < 2) continue;
                    bool nextReversed = startReversed ^ (((i + 1) & 1) == 1);
                    Vec3 nextEntry = nextReversed ? next.Points[^1] : next.Points[0];
                    double nextEntryHeading = nextReversed
                        ? next.Points[^1].Heading + Math.PI
                        : next.Points[0].Heading;

                    var turn = _turnPathService.GenerateTurn(new TurnPathInput
                    {
                        ExitPoint = exitPose,
                        ExitHeading = exitPose.Heading,
                        EntryPoint = new Vec3(nextEntry.Easting, nextEntry.Northing, nextEntryHeading),
                        EntryHeading = nextEntryHeading,
                        TurningRadius = turningRadius,
                        HeadlandWidth = headlandWidth,
                        Boundary = outerBoundary,
                        InnerBoundaries = innerBoundaries,
                    });

                    plan.Segments.Add(new RouteSegment
                    {
                        Type = RouteSegmentType.Turn,
                        SwathIndex = globalSwathIndex - 1,
                        Waypoints = turn.Waypoints,
                        Length = turn.Length,
                        IsTurnValid = turn.IsValid,
                        TurnPathType = turn.PathType,
                    });
                }

                prevExitPose = exitPose;
            }
        }

        return plan;
    }

    private static RouteSegment MakeTurn(TurnPathResult result, int swathFromIndex) => new()
    {
        Type = RouteSegmentType.Turn,
        SwathIndex = swathFromIndex,
        Waypoints = result.Waypoints,
        Length = result.Length,
        IsTurnValid = result.IsValid,
        TurnPathType = result.PathType,
    };

    private static RouteSegment MakeTransit(TransitPathResult result, int swathFromIndex) => new()
    {
        Type = RouteSegmentType.Transit,
        SwathIndex = swathFromIndex,
        Waypoints = result.Waypoints,
        Length = result.Length,
        IsTurnValid = result.IsValid,
        TurnPathType = result.CircuitUsed,
    };
}
