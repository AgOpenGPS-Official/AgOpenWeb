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
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class CellTraversalPlannerTests
{
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
    public void Rectangle_VisitsTheSingleCell()
    {
        var graph = new CellDecompositionService().Decompose(
            Rect(0, 10, 0, 10), new List<BoundaryPolygon>(), System.Math.PI / 2);

        var swathGen = new CellSwathGenerator();
        var swaths = new Dictionary<int, List<Models.Track.Track>>();
        foreach (var c in graph.Cells)
            swaths[c.Id] = swathGen.Generate(c, System.Math.PI / 2, toolWidth: 2.0, overlap: 0.0);

        var planner = new CellTraversalPlanner();
        var visits = planner.Plan(
            graph.Cells, swaths,
            startPosition: new Vec2(-1, 0),
            startHeading: 0,
            swathHeading: 0,
            turningRadius: 5.0);

        Assert.That(visits.Count, Is.EqualTo(1), "single-cell decomposition gets one visit");
        Assert.That(visits[0].SwathsInTraversalOrder.Count, Is.EqualTo(5));
    }

    [Test]
    public void RectangleWithHole_VisitsAllFourCells()
    {
        var graph = new CellDecompositionService().Decompose(
            Rect(0, 10, 0, 10), new List<BoundaryPolygon> { Rect(3, 5, 2, 4) },
            System.Math.PI / 2);
        Assert.That(graph.Cells.Count, Is.EqualTo(4));

        var swathGen = new CellSwathGenerator();
        var swaths = new Dictionary<int, List<Models.Track.Track>>();
        foreach (var c in graph.Cells)
            swaths[c.Id] = swathGen.Generate(c, System.Math.PI / 2, toolWidth: 1.0, overlap: 0.0);

        var planner = new CellTraversalPlanner();
        var visits = planner.Plan(
            graph.Cells, swaths,
            startPosition: new Vec2(-1, 0),
            startHeading: 0,
            swathHeading: 0,
            turningRadius: 5.0);

        // Should visit all 4 cells (those with at least one swath).
        var cellsWithSwaths = swaths.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToHashSet();
        Assert.That(visits.Count, Is.EqualTo(cellsWithSwaths.Count));

        // Each cell visited exactly once.
        var visitedCellIds = visits.Select(v => v.CellId).ToHashSet();
        Assert.That(visitedCellIds.Count, Is.EqualTo(visits.Count), "no cell visited twice");
        foreach (var cellId in cellsWithSwaths)
            Assert.That(visitedCellIds.Contains(cellId), "all populated cells visited");
    }
}
