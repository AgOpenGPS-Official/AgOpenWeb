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

    [Test]
    public void RectangleWithHole_NoCellOverlapsObstacle()
    {
        // Regression: cells must not contain points strictly inside the obstacle.
        // This catches the bug where cell polygons were built without proper
        // floor/ceiling segments, producing triangular slivers near the obstacle
        // that included obstacle area.
        var outer = Rect(0, 10, 0, 10);
        var hole = Rect(3, 5, 2, 4);
        var graph = new CellDecompositionService().Decompose(
            outer, new List<AgValoniaGPS.Models.BoundaryPolygon> { hole }, SweepNorth);

        var holeVec2 = new List<AgValoniaGPS.Models.Base.Vec2>
        {
            new(3, 2), new(5, 2), new(5, 4), new(3, 4)
        };
        foreach (var cell in graph.Cells)
        {
            double cx = 0, cy = 0;
            foreach (var p in cell.Polygon) { cx += p.Easting; cy += p.Northing; }
            cx /= cell.Polygon.Count;
            cy /= cell.Polygon.Count;
            bool inHole = AgValoniaGPS.Models.Base.GeometryMath.IsPointInPolygon(holeVec2,
                new AgValoniaGPS.Models.Base.Vec2(cx, cy));
            Assert.That(inHole, Is.False,
                $"Cell {cell.Id} centroid ({cx:F2}, {cy:F2}) is inside the obstacle");
        }
    }

    [Test]
    public void RectangleWithInteriorHole_ProducesThreeCells()
    {
        // Outer 10x10 with a small interior hole [3,5]x[2,4].
        // Sweep north → expected:
        //   - Cell 0: bottom (below hole)         ── closed at SPLIT (3,2)
        //   - Cell 1: left strip (alongside hole) ─┐
        //   - Cell 2: right strip                  ├ closed at MERGE (5,4)
        //   - Cell 3: top (above hole)            ── closed at CLOSE (10,10)
        var outer = Rect(0, 10, 0, 10);
        var hole = Rect(3, 5, 2, 4);

        var graph = new CellDecompositionService().Decompose(
            outer, new List<BoundaryPolygon> { hole }, SweepNorth);

        // 4 cells, 4 Reeb edges (split adds 2, merge adds 2)
        Assert.That(graph.Cells.Count, Is.EqualTo(4),
            "rectangle-with-hole should decompose into bottom + left + right + top");
        Assert.That(graph.Edges.Count, Is.EqualTo(4),
            "split contributes 2 edges (parent→L, parent→R), merge contributes 2 (L→C, R→C)");

        // Each cell should be a valid polygon (≥3 vertices).
        foreach (var c in graph.Cells)
            Assert.That(c.Polygon.Count, Is.GreaterThanOrEqualTo(3));
    }
}
