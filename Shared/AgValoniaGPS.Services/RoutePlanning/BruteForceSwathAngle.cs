// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Brute-force optimal swath direction: try every angle in <c>[0, π)</c> at
/// <c>stepDegrees</c> resolution and return the heading minimizing the chosen
/// objective. Equivalent to F2C's <c>BruteForce::computeBestAngle</c>.
///
/// Lines are bidirectional, so the search domain is half a circle.
/// 1° resolution × 180 evaluations is cheap (~milliseconds per cell) and
/// matches F2C's default.
/// </summary>
public static class BruteForceSwathAngle
{
    private static readonly ISwathAngleObjective DefaultObjective = new MinTurnCountObjective();

    public static double FindOptimalHeading(
        IReadOnlyList<Vec2> cellOuter,
        double opWidth,
        ISwathAngleObjective? objective = null,
        double stepDegrees = 1.0)
    {
        if (cellOuter == null || cellOuter.Count < 3 || opWidth <= 0) return 0.0;
        if (stepDegrees <= 0) throw new ArgumentException("stepDegrees must be > 0", nameof(stepDegrees));

        objective ??= DefaultObjective;
        double bestHeading = 0;
        double bestCost = double.PositiveInfinity;

        int steps = (int)Math.Round(180.0 / stepDegrees);
        for (int i = 0; i < steps; i++)
        {
            double heading = i * stepDegrees * Math.PI / 180.0;
            double cost = objective.Cost(cellOuter, heading, opWidth);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestHeading = heading;
            }
        }
        return bestHeading;
    }

    public static double FindOptimalHeading(
        Cell cell,
        double opWidth,
        ISwathAngleObjective? objective = null,
        double stepDegrees = 1.0)
        => FindOptimalHeading(cell.Polygon, opWidth, objective, stepDegrees);
}
