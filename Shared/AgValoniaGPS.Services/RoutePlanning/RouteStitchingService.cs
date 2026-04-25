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

        double heading = config.ReferenceHeading;

        for (int i = 0; i < swaths.Count; i++)
        {
            var swath = swaths[i];
            if (swath.Points.Count < 2) continue;

            // Determine travel direction: even swaths forward, odd swaths reverse
            bool isReverse = i % 2 != 0;
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

            // Exit/entry points depend on alternating direction
            Vec3 exitPoint, entryPoint;
            double exitHeading, entryHeading;

            if (i % 2 == 0)
            {
                // Current swath driven forward: exit from end
                exitPoint = swath.Points[^1];
                // Next swath driven reverse: enter from end
                entryPoint = next.Points[^1];
                exitHeading = heading;
                entryHeading = heading + Math.PI;
            }
            else
            {
                // Current swath driven reverse: exit from start
                exitPoint = swath.Points[0];
                // Next swath driven forward: enter from start
                entryPoint = next.Points[0];
                exitHeading = heading + Math.PI;
                entryHeading = heading;
            }

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
