// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class CellSwathGeneratorTests
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
    public void Rectangle_ProducesEvenlySpacedSwaths()
    {
        // 10x10 rectangle, sweep east (heading π/2), tool width 2 → 5 swaths
        // running perpendicular to sweep (= north-south).
        var graph = new CellDecompositionService().Decompose(
            Rect(0, 10, 0, 10), new List<BoundaryPolygon>(), System.Math.PI / 2);
        Assert.That(graph.Cells.Count, Is.EqualTo(1));

        var gen = new CellSwathGenerator();
        var swaths = gen.Generate(graph.Cells[0], System.Math.PI / 2, toolWidth: 2.0, overlap: 0.0);

        Assert.That(swaths.Count, Is.EqualTo(5), "10m wide rectangle / 2m tool = 5 swaths");
        foreach (var swath in swaths)
        {
            Assert.That(swath.Points.Count, Is.EqualTo(2));
            // Each swath spans from min northing to max northing (≈10m long).
            double dn = System.Math.Abs(swath.Points[0].Northing - swath.Points[1].Northing);
            Assert.That(dn, Is.EqualTo(10.0).Within(0.1), "swath length should match polygon extent");
        }
    }

    [Test]
    public void RectangleWithHole_GeneratesUninterruptedSwathsPerCell()
    {
        // 10x10 outer with [3,5]x[2,4] hole, sweep east. BCD produces 4 cells.
        // Each cell, being monotone, generates swaths with no obstacle splits.
        var graph = new CellDecompositionService().Decompose(
            Rect(0, 10, 0, 10), new List<BoundaryPolygon> { Rect(3, 5, 2, 4) },
            System.Math.PI / 2);

        Assert.That(graph.Cells.Count, Is.EqualTo(4));

        var gen = new CellSwathGenerator();
        int totalSwaths = 0;
        foreach (var cell in graph.Cells)
        {
            var swaths = gen.Generate(cell, System.Math.PI / 2, toolWidth: 1.0, overlap: 0.0);
            // Every swath should be a 2-point line (uninterrupted within cell).
            foreach (var swath in swaths) Assert.That(swath.Points.Count, Is.EqualTo(2));
            totalSwaths += swaths.Count;
        }

        // Sanity: should generate a reasonable number of swaths total (at least
        // 1 per cell for a non-trivial decomposition).
        Assert.That(totalSwaths, Is.GreaterThan(0));
    }
}
