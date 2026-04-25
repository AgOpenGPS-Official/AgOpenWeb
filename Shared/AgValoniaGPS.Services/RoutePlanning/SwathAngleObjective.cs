// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Strategy interface for scoring a candidate swath direction within a cell.
/// Lower cost is better. Implementations correspond to F2C's <c>SGObjective</c>
/// family (see <c>swath_generator/cost_function</c>).
/// </summary>
public interface ISwathAngleObjective
{
    /// <param name="cellOuter">Outer ring of the cell (any orientation).</param>
    /// <param name="swathHeading">
    ///   Candidate swath direction in our heading convention (radians, from
    ///   +N CW). Lines are bidirectional, so the search range is [0, π).
    /// </param>
    /// <param name="opWidth">Implement / swath spacing in meters.</param>
    double Cost(IReadOnlyList<Vec2> cellOuter, double swathHeading, double opWidth);
}

/// <summary>
/// Minimize the number of swaths needed to cover the cell. Equivalent to F2C's
/// <c>NSwath</c> objective. Naturally favors swath direction along the cell's
/// long axis (which has the smallest perpendicular extent). This is the
/// default objective for boustrophedon planning.
/// </summary>
public sealed class MinTurnCountObjective : ISwathAngleObjective
{
    public double Cost(IReadOnlyList<Vec2> cellOuter, double swathHeading, double opWidth)
    {
        if (cellOuter == null || cellOuter.Count < 3 || opWidth <= 0) return 0.0;
        // Use the raw ratio rather than Math.Ceiling: the optimum heading is
        // identical (cost is monotonic in extent) and we avoid a nasty
        // floating-point pitfall where extent works out to 5.000…6 at the
        // exact axis-aligned angle and ceils to 6 instead of 5. Callers that
        // need an integer swath count can take ⌈Cost⌉ themselves.
        return PerpendicularExtent(cellOuter, swathHeading) / opWidth;
    }

    /// <summary>
    /// Width of the cell measured perpendicular to the swath direction.
    /// </summary>
    public static double PerpendicularExtent(IReadOnlyList<Vec2> polygon, double swathHeading)
    {
        // Swath direction unit = (sin h, cos h) in (E, N). Perpendicular to it
        // is (cos h, −sin h) (rotated −90°). Project each vertex.
        double pe = Math.Cos(swathHeading);
        double pn = -Math.Sin(swathHeading);
        double pMin = double.PositiveInfinity, pMax = double.NegativeInfinity;
        for (int i = 0; i < polygon.Count; i++)
        {
            double pp = polygon[i].Easting * pe + polygon[i].Northing * pn;
            if (pp < pMin) pMin = pp;
            if (pp > pMax) pMax = pp;
        }
        return pMax - pMin;
    }
}
