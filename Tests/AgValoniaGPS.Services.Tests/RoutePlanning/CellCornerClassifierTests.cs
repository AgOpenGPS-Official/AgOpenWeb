// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class CellCornerClassifierTests
{
    private const double SweepNorth = 0.0;

    private static List<Vec2> Rect(double minE, double maxE, double minN, double maxN)
        => new() { new(minE, minN), new(maxE, minN), new(maxE, maxN), new(minE, maxN) };

    [Test]
    public void Rectangle_AllFourCornersAreHeadland()
    {
        var outer = Rect(0, 10, 0, 10);
        var cells = BoustrophedonDecomp.Decompose(outer, new List<List<Vec2>>(), SweepNorth);

        CellCornerClassifier.ClassifyAll(cells, outer, SweepNorth);

        Assert.That(cells.Count, Is.EqualTo(1));
        foreach (var k in cells[0].CornerKinds)
            Assert.That(k, Is.EqualTo(CellCornerKind.OuterHeadland));
    }

    [Test]
    public void LShape_AllCornersHeadland_BecauseCutsLandOnOuter()
    {
        // Both endpoints of the cut at (5,5)→(0,5) lie on the outer L
        // boundary (the reflex vertex itself, and the left edge at y=5),
        // so every cell corner maps to a vertex on the outer.
        var l = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10),
        };

        var cells = BoustrophedonDecomp.Decompose(l, new List<List<Vec2>>(), SweepNorth);
        CellCornerClassifier.ClassifyAll(cells, l, SweepNorth);

        Assert.That(cells.Count, Is.EqualTo(2));
        foreach (var c in cells)
            foreach (var k in c.CornerKinds)
                Assert.That(k, Is.EqualTo(CellCornerKind.OuterHeadland),
                    $"Cell {c.Id} should have all-headland corners");
    }

    [Test]
    public void RectangleWithTopologicalHole_SideStripsHaveTwoInternalCorners()
    {
        // Hole at (3,4)-(5,4)-(5,6)-(3,6) splits the rectangle into 4 cells.
        // Bottom and top cells span the full width — all corners on outer.
        // Left and right side strips touch the hole on one side: their two
        // hole-side corners sit at hole vertices (interior, INTERNAL); their
        // two outer-side corners sit on the rectangle's left/right edges
        // (HEADLAND).
        var outer = Rect(0, 10, 0, 10);
        var hole = Rect(3, 5, 4, 6);

        var cells = BoustrophedonDecomp.Decompose(
            outer, new List<List<Vec2>> { hole }, SweepNorth);
        CellCornerClassifier.ClassifyAll(cells, outer, SweepNorth);

        Assert.That(cells.Count, Is.EqualTo(4));

        // Count cells by their HEADLAND/INTERNAL composition.
        int allHeadland = 0;
        int twoEach = 0;
        foreach (var c in cells)
        {
            int h = c.CornerKinds.Count(k => k == CellCornerKind.OuterHeadland);
            int i = c.CornerKinds.Count(k => k == CellCornerKind.Internal);
            if (h == 4) allHeadland++;
            else if (h == 2 && i == 2) twoEach++;
        }

        Assert.That(allHeadland, Is.EqualTo(2),
            "bottom and top cells span outer top-to-bottom — all-headland");
        Assert.That(twoEach, Is.EqualTo(2),
            "left and right side strips bordering the hole — two HEADLAND + two INTERNAL");
    }

    [Test]
    public void NotchedOuter_AllCornersHeadland()
    {
        // The notch's two reflex vertices and the outer-edge intersections
        // are all on the outer polygon, so every cell corner is HEADLAND.
        var notched = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 10), new(6, 10),
            new(6, 6), new(4, 6), new(4, 10), new(0, 10),
        };
        var cells = BoustrophedonDecomp.Decompose(notched, new List<List<Vec2>>(), SweepNorth);
        CellCornerClassifier.ClassifyAll(cells, notched, SweepNorth);

        foreach (var c in cells)
            foreach (var k in c.CornerKinds)
                Assert.That(k, Is.EqualTo(CellCornerKind.OuterHeadland));
    }

    [Test]
    public void EmptyCells_ClassifierIsNoOp()
    {
        // Defensive: classifier must tolerate empty/missing inputs.
        Assert.DoesNotThrow(() => CellCornerClassifier.ClassifyAll(
            new List<Cell>(), Rect(0, 1, 0, 1), SweepNorth));
        Assert.DoesNotThrow(() => CellCornerClassifier.ClassifyAll(
            new List<Cell> { new() }, Rect(0, 1, 0, 1), SweepNorth));
    }

    [Test]
    public void RectangleWithExpandedHole_HoleSideCornersAreInnerHeadland()
    {
        // Same geometry as RectangleWithTopologicalHole_SideStripsHaveTwoInternalCorners,
        // but now the hole is decomposed against AND classified against. The
        // hole-side cell corners now sit on the hole's expanded ring (which
        // happens to be the same ring since we don't expand it in this test),
        // so they get tagged InnerHeadland instead of Internal.
        var outer = Rect(0, 10, 0, 10);
        var hole = Rect(3, 5, 4, 6);
        var holes = new List<List<Vec2>> { hole };

        var cells = BoustrophedonDecomp.Decompose(outer, holes, SweepNorth);
        CellCornerClassifier.ClassifyAll(cells, outer, holes, SweepNorth);

        Assert.That(cells.Count, Is.EqualTo(4));

        int allOuter = 0;
        int twoOuterTwoInner = 0;
        foreach (var c in cells)
        {
            int o = c.CornerKinds.Count(k => k == CellCornerKind.OuterHeadland);
            int i = c.CornerKinds.Count(k => k == CellCornerKind.InnerHeadland);
            int n = c.CornerKinds.Count(k => k == CellCornerKind.Internal);
            if (o == 4) allOuter++;
            else if (o == 2 && i == 2 && n == 0) twoOuterTwoInner++;
        }

        Assert.That(allOuter, Is.EqualTo(2),
            "bottom and top cells span outer top-to-bottom — all-OuterHeadland");
        Assert.That(twoOuterTwoInner, Is.EqualTo(2),
            "left and right side strips — two OuterHeadland + two InnerHeadland");
    }
}
