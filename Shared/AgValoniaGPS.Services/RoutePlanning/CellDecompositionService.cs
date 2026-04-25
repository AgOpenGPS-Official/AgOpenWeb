// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Boustrophedon Cellular Decomposition entry point. Delegates to
/// <see cref="BcdSweep"/> which implements the sweep-line algorithm.
/// </summary>
public class CellDecompositionService : ICellDecompositionService
{
    public ReebGraph Decompose(BoundaryPolygon outer, List<BoundaryPolygon> inner, double sweepHeading)
    {
        if (outer.Points.Count < 3) return new ReebGraph();
        return new BcdSweep().Run(outer, inner, sweepHeading);
    }
}
