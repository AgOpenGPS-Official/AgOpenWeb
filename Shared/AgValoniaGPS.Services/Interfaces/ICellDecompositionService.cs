// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Boustrophedon Cellular Decomposition (Choset 2000) for a polygon-with-holes.
/// Returns a Reeb graph: convex cells (each traversable by uninterrupted
/// boustrophedon) plus their adjacency at critical points.
/// </summary>
public interface ICellDecompositionService
{
    /// <summary>
    /// Decompose the field minus inner boundaries into convex cells.
    /// </summary>
    /// <param name="outer">Outer field boundary (or clip boundary if a headland is in use).</param>
    /// <param name="inner">Inner boundaries (obstacles) to exclude.</param>
    /// <param name="sweepHeading">
    /// Sweep direction in radians (typically perpendicular to the swath direction).
    /// Critical points are classified relative to this direction.
    /// </param>
    ReebGraph Decompose(BoundaryPolygon outer, List<BoundaryPolygon> inner, double sweepHeading);
}
