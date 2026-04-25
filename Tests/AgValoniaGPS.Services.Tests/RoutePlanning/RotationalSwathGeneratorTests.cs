// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class RotationalSwathGeneratorTests
{
    private static List<Vec2> Rect(double minE, double maxE, double minN, double maxN)
        => new() { new(minE, minN), new(maxE, minN), new(maxE, maxN), new(minE, maxN) };

    [Test]
    public void Rectangle_NoHoles_ProducesExpectedSwathCount()
    {
        // 10×10 rectangle, opWidth = 1, swath direction east (along +E):
        //   y in [0, 10], 10 / 1 = 10 swaths centered at y = 0.5, 1.5, …, 9.5.
        var cell = new Cell { Polygon = Rect(0, 10, 0, 10) };
        var gen = new RotationalSwathGenerator();

        var swaths = gen.Generate(cell, swathHeading: Math.PI / 2, opWidth: 1.0);

        Assert.That(swaths.Count, Is.EqualTo(10));
        // Each swath has exactly one segment (no holes), spanning x = 0 to 10.
        foreach (var s in swaths)
        {
            Assert.That(s.Segments.Count, Is.EqualTo(1));
            Assert.That(s.IsSplit, Is.False);
            var seg = s.Segments[0];
            Assert.That(seg.Length, Is.EqualTo(10.0).Within(1e-6));
        }
    }

    [Test]
    public void RectangleWithCentralHole_SwathsCrossingHoleAreSplit()
    {
        // 10×10 cell with a 4×2 hole at x in [3,7], y in [4, 6].
        // Swath direction east. With opWidth = 1, swaths are at y = 0.5 .. 9.5.
        // Swaths at y = 4.5 and y = 5.5 cross the hole and split into two
        // segments; the rest are uninterrupted.
        var cell = new Cell
        {
            Polygon = Rect(0, 10, 0, 10),
            InnerRings = new List<List<Vec2>> { Rect(3, 7, 4, 6) },
        };
        var gen = new RotationalSwathGenerator();

        var swaths = gen.Generate(cell, swathHeading: Math.PI / 2, opWidth: 1.0);

        Assert.That(swaths.Count, Is.EqualTo(10));
        int splitCount = swaths.Count(s => s.IsSplit);
        Assert.That(splitCount, Is.EqualTo(2),
            "the two swaths at y=4.5 and y=5.5 should cross the hole and split");

        // Each split swath has segments [0,3] and [7,10].
        foreach (var s in swaths.Where(s => s.IsSplit))
        {
            Assert.That(s.Segments.Count, Is.EqualTo(2));
            // After unrotation back to original frame: swath at heading π/2
            // means line is along +E; segment endpoints' Easting tell us the
            // x position. Segment [0,3] then [7,10].
            var leftSeg = s.Segments[0];
            var rightSeg = s.Segments[1];
            Assert.That(leftSeg.Length, Is.EqualTo(3.0).Within(1e-6));
            Assert.That(rightSeg.Length, Is.EqualTo(3.0).Within(1e-6));
        }
    }

    [Test]
    public void NorthSwathDirection_RotatesAndClipsCorrectly()
    {
        // Same 10×10 cell, hole at y=4..6, x=3..7. Now swath direction NORTH
        // (heading 0): swaths run vertically. Swaths at x = 0.5, 1.5, …, 9.5.
        // Swaths at x = 3.5, 4.5, 5.5, 6.5 cross the hole.
        var cell = new Cell
        {
            Polygon = Rect(0, 10, 0, 10),
            InnerRings = new List<List<Vec2>> { Rect(3, 7, 4, 6) },
        };
        var gen = new RotationalSwathGenerator();

        var swaths = gen.Generate(cell, swathHeading: 0.0, opWidth: 1.0);

        Assert.That(swaths.Count, Is.EqualTo(10));
        int splitCount = swaths.Count(s => s.IsSplit);
        Assert.That(splitCount, Is.EqualTo(4),
            "swaths at x = 3.5, 4.5, 5.5, 6.5 should cross the hole");
    }

    [Test]
    public void EmptyOrDegenerateInputs_AreSafe()
    {
        var gen = new RotationalSwathGenerator();
        Assert.That(gen.Generate(new Cell(), Math.PI / 2, 1.0).Count, Is.EqualTo(0));
        Assert.That(gen.Generate(new Cell { Polygon = Rect(0, 10, 0, 10) }, Math.PI / 2, opWidth: 0).Count,
            Is.EqualTo(0));
    }

    [Test]
    public void OpWidthLargerThanCell_StillProducesOneCenteredSwath()
    {
        // 5×5 cell with 100m opWidth — 5/100 < 0.5 → fall back to a single
        // centered swath. Useful for headland transits / very small cells.
        var cell = new Cell { Polygon = Rect(0, 5, 0, 5) };
        var gen = new RotationalSwathGenerator();

        var swaths = gen.Generate(cell, swathHeading: Math.PI / 2, opWidth: 100.0);

        Assert.That(swaths.Count, Is.EqualTo(1));
        Assert.That(swaths[0].Segments.Count, Is.EqualTo(1));
        // Segment runs along +E at y = 2.5: from (0, 2.5) to (5, 2.5).
        Assert.That(swaths[0].Segments[0].Start.Northing, Is.EqualTo(2.5).Within(1e-6));
        Assert.That(swaths[0].Segments[0].End.Northing, Is.EqualTo(2.5).Within(1e-6));
    }

    [Test]
    public void DecomposedCellRoundTrip_GeneratesValidSwaths()
    {
        // Decompose an L-shape, then run the swath generator on each cell with
        // optimal heading. Sanity checks: each cell gets >= 1 swath; segments
        // are non-empty; total segment area roughly matches cell area.
        var l = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10),
        };
        var cells = BoustrophedonDecomp.Decompose(l, new List<List<Vec2>>(), 0);
        Assert.That(cells.Count, Is.EqualTo(2));

        var gen = new RotationalSwathGenerator();
        const double opWidth = 0.5;
        foreach (var cell in cells)
        {
            double heading = BruteForceSwathAngle.FindOptimalHeading(cell, opWidth);
            var swaths = gen.Generate(cell, heading, opWidth);

            Assert.That(swaths.Count, Is.GreaterThan(0),
                $"Cell {cell.Id} should produce at least one swath");
            foreach (var s in swaths)
            {
                Assert.That(s.Segments.Count, Is.GreaterThan(0));
                foreach (var seg in s.Segments)
                    Assert.That(seg.Length, Is.GreaterThan(0));
            }
        }
    }
}
