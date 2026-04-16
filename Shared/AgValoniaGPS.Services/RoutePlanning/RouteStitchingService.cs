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
/// For each swath pair, calls TurnPathService to generate a boundary-validated
/// Dubins turn. The result is an alternating sequence: swath, turn, swath, turn, ...
/// </summary>
public class RouteStitchingService : IRouteStitchingService
{
    private readonly ITurnPathService _turnPathService;

    public RouteStitchingService(ITurnPathService turnPathService)
    {
        _turnPathService = turnPathService;
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

            // Generate turn to next swath (if not the last swath).
            // Skip if the next track is a sibling segment of the same original swath
            // (split by an inner boundary) — no turn between pieces of the same line.
            bool skipTurn = sourceSwathIndex != null
                && i + 1 < swaths.Count
                && i < sourceSwathIndex.Count
                && i + 1 < sourceSwathIndex.Count
                && sourceSwathIndex[i] == sourceSwathIndex[i + 1];

            if (i < swaths.Count - 1 && !skipTurn)
            {
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

                var turnInput = new TurnPathInput
                {
                    ExitPoint = exitPoint,
                    ExitHeading = exitHeading,
                    EntryPoint = entryPoint,
                    EntryHeading = entryHeading,
                    TurningRadius = config.TurningRadius,
                    HeadlandWidth = config.HeadlandWidth,
                    Boundary = config.Boundary,
                };

                var turnResult = _turnPathService.GenerateTurn(turnInput);

                plan.Segments.Add(new RouteSegment
                {
                    Type = RouteSegmentType.Turn,
                    SwathIndex = i, // Turn from swath i to i+1
                    Waypoints = turnResult.Waypoints,
                    Length = turnResult.Length,
                    IsTurnValid = turnResult.IsValid,
                    TurnPathType = turnResult.PathType,
                });
            }
        }

        return plan;
    }
}
