// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgValoniaGPS.Models;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class BcdSweepTests
{
    private const double SweepNorth = 0.0;

    private static BoundaryPolygon Rect(double minE, double maxE, double minN, double maxN)
    {
        var bp = new BoundaryPolygon();
        bp.Points.Add(new BoundaryPoint { Easting = minE, Northing = minN });
        bp.Points.Add(new BoundaryPoint { Easting = maxE, Northing = minN });
        bp.Points.Add(new BoundaryPoint { Easting = maxE, Northing = maxN });
        bp.Points.Add(new BoundaryPoint { Easting = minE, Northing = maxN });
        bp.UpdateBounds();
        return bp;
    }

    [Test]
    public void Rectangle_ProducesSingleCell()
    {
        var outer = Rect(0, 10, 0, 10);
        var graph = new CellDecompositionService().Decompose(
            outer, new List<BoundaryPolygon>(), SweepNorth);

        Assert.That(graph.Cells.Count, Is.EqualTo(1), "Convex rectangle should be one cell");
        Assert.That(graph.Edges.Count, Is.EqualTo(0), "Single cell has no Reeb adjacencies");

        var cell = graph.Cells[0];
        Assert.That(cell.Polygon.Count, Is.GreaterThanOrEqualTo(3));
        // Sweep extent matches polygon's y-range.
        Assert.That(cell.SweepStart, Is.EqualTo(0.0).Within(1.0));
        Assert.That(cell.SweepEnd, Is.EqualTo(10.0).Within(1.0));
    }

    [Test]
    public void EmptyPolygon_ReturnsEmptyGraph()
    {
        var bp = new BoundaryPolygon(); // no points
        var graph = new CellDecompositionService().Decompose(
            bp, new List<BoundaryPolygon>(), SweepNorth);

        Assert.That(graph.Cells.Count, Is.EqualTo(0));
        Assert.That(graph.Edges.Count, Is.EqualTo(0));
    }
}
