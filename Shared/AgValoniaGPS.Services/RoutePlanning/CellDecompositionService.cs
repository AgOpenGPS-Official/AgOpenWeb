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
/// Phase A scaffolding — returns a single-cell decomposition (whole outer polygon
/// as one cell, no obstacle handling). Phase B/C will replace this with the real
/// sweep-line algorithm.
///
/// For fields without obstacles BCD reduces to one cell, so the stub is correct
/// behavior for the simple case.
/// </summary>
public class CellDecompositionService : ICellDecompositionService
{
    public ReebGraph Decompose(BoundaryPolygon outer, List<BoundaryPolygon> inner, double sweepHeading)
    {
        var graph = new ReebGraph();
        if (outer.Points.Count < 3) return graph;

        // Project polygon vertices onto the sweep direction so the cell can
        // record its sweep-coordinate extent — Phase E uses this to size the
        // per-cell swath grid.
        double sx = System.Math.Sin(sweepHeading);
        double sy = System.Math.Cos(sweepHeading);

        var poly = outer.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();

        double sweepStart = double.PositiveInfinity;
        double sweepEnd = double.NegativeInfinity;
        foreach (var pt in poly)
        {
            double s = pt.Easting * sx + pt.Northing * sy;
            if (s < sweepStart) sweepStart = s;
            if (s > sweepEnd) sweepEnd = s;
        }

        graph.Cells.Add(new Cell
        {
            Id = 0,
            Polygon = poly,
            SweepStart = sweepStart,
            SweepEnd = sweepEnd,
        });

        return graph;
    }
}
