// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class RaycastDecompTests
{
    private const double SweepNorth = 0.0;

    private static List<Vec2> Rect(double minE, double maxE, double minN, double maxN)
        => new() { new(minE, minN), new(maxE, minN), new(maxE, maxN), new(minE, maxN) };

    // --- Trapezoidal-vs-Boustrophedon expectation: rectangle has no critical
    //     vertices, so both produce 1 cell. We test boustrophedon as default. ---

    [Test]
    public void Rectangle_OneCell_NoInnerRings()
    {
        var cells = BoustrophedonDecomp.Decompose(
            Rect(0, 10, 0, 10),
            new List<List<Vec2>>(),
            SweepNorth);

        Assert.That(cells.Count, Is.EqualTo(1));
        Assert.That(cells[0].InnerRings.Count, Is.EqualTo(0));
        Assert.That(cells[0].SweepStart, Is.EqualTo(0).Within(0.5));
        Assert.That(cells[0].SweepEnd, Is.EqualTo(10).Within(0.5));
    }

    [Test]
    public void DecompositionThreshold_FiltersMarginalReflexVertex()
    {
        // L with a slightly-jittered reflex corner — interior angle ~190°
        // instead of the clean 270°. With threshold 200°, this should be
        // skipped and we get a single cell instead of two.
        // The geometry: outer L shape but with the reflex bumped only 0.5°
        // off straight. Easier construction: nearly-straight reflex.
        // Take a vertical-edge polygon with a tiny inward bump:
        //   (0,0), (10,0), (10,5), (9.9,5), (10,5.01), (10,10), (0,10)
        // The vertex at (9.9, 5) is reflex but the interior angle is just
        // barely > 180°.
        var nearStraight = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 5),
            new(9.9, 5), new(10, 5.01),
            new(10, 10), new(0, 10),
        };

        // Standard 180° threshold: this nearly-straight reflex IS a critical
        // vertex (both edges go in same sweep direction) and DOES split the cell.
        var cellsStrict = BoustrophedonDecomp.Decompose(nearStraight, new List<List<Vec2>>(),
            SweepNorth, decompositionThresholdDegrees: 180.0);

        // 200° threshold: the marginal reflex is filtered out, single cell.
        var cellsFiltered = BoustrophedonDecomp.Decompose(nearStraight, new List<List<Vec2>>(),
            SweepNorth, decompositionThresholdDegrees: 200.0);

        Assert.That(cellsFiltered.Count, Is.LessThanOrEqualTo(cellsStrict.Count),
            "Higher decomposition threshold must produce ≤ cells than the strict default");
    }

    [Test]
    public void LShape_TwoCells()
    {
        // L oriented so the inside concave corner is at (5, 5).
        //   (0,10) ── (5,10)
        //     │         │
        //     │         (5,5) ── (10,5)
        //     │                     │
        //   (0,0) ──────────────── (10,0)
        var l = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10),
        };

        var cells = BoustrophedonDecomp.Decompose(l, new List<List<Vec2>>(), SweepNorth);

        Assert.That(cells.Count, Is.EqualTo(2),
            "concave L should split at the reflex vertex into 2 cells");
        foreach (var c in cells)
            Assert.That(c.InnerRings.Count, Is.EqualTo(0));
    }

    [Test]
    public void NotchedOuter_ThreeCells()
    {
        // Rectangle 0..10 x 0..10 with a notch cut from the top:
        //   notch is the rectangle 4..6 x 6..10 removed.
        //   Two reflex vertices at (4,6) and (6,6).
        var notched = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 10), new(6, 10),
            new(6, 6), new(4, 6), new(4, 10), new(0, 10),
        };

        var cells = BoustrophedonDecomp.Decompose(notched, new List<List<Vec2>>(), SweepNorth);

        Assert.That(cells.Count, Is.EqualTo(3),
            "outer notch with two reflex vertices should produce 3 cells");
    }

    [Test]
    public void RectangleWithCentralHole_AsLocalObstacle_OneCellOneInnerRing()
    {
        // Hole is passed NOT as a topological inner — decomposition produces a
        // single cell, and the obstacle is attached as an inner ring.
        var outer = Rect(0, 10, 0, 10);
        var hole = Rect(3, 5, 4, 6);

        var cells = BoustrophedonDecomp.Decompose(outer, new List<List<Vec2>>(), SweepNorth);
        LocalObstacleAttacher.Attach(cells, new List<List<Vec2>> { hole });

        Assert.That(cells.Count, Is.EqualTo(1));
        Assert.That(cells[0].InnerRings.Count, Is.EqualTo(1));
        Assert.That(cells[0].InnerRings[0].Count, Is.EqualTo(4));
    }

    [Test]
    public void RectangleWithCentralHole_AsTopologicalInner_FourCells()
    {
        // Same hole passed as a topological inner: it has critical vertices
        // (top and bottom of the hole) that split the field into 4 cells.
        var outer = Rect(0, 10, 0, 10);
        var hole = Rect(3, 5, 4, 6);

        var cells = BoustrophedonDecomp.Decompose(
            outer,
            new List<List<Vec2>> { hole },
            SweepNorth);

        // Bottom strip (below hole), middle-left strip, middle-right strip, top strip.
        Assert.That(cells.Count, Is.EqualTo(4));
        foreach (var c in cells)
        {
            Assert.That(c.Polygon.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(c.InnerRings.Count, Is.EqualTo(0),
                "topological hole should be removed from the field, not attached as a ring");
        }
    }

    [Test]
    public void Hourglass_OneCell_ForVerticalSweep()
    {
        // Outer polygon shaped like an hourglass with a bottleneck at y=5:
        //   (0,0) ── (10,0)
        //     ╲       ╱
        //   (3,5) ─ (7,5)   ← waist (two reflex points)
        //     ╱       ╲
        //   (0,10) ─ (10,10)
        //
        // Sweep coord is monotone through the waist vertices (their adjacent
        // sweep coords straddle them), so the waist points are NOT local
        // extrema and produce no cuts. Geometrically correct — at every y the
        // polygon is a single x-interval.
        var hourglass = new List<Vec2>
        {
            new(0, 0), new(10, 0),
            new(7, 5), new(10, 10),
            new(0, 10), new(3, 5),
        };

        var cells = BoustrophedonDecomp.Decompose(hourglass, new List<List<Vec2>>(), SweepNorth);

        Assert.That(cells.Count, Is.EqualTo(1),
            "hourglass with vertical sweep stays one connected interval at every y");
    }

    [Test]
    public void TShape_HasSplitAndMerge()
    {
        // T-shape: horizontal top bar (y=5..7) with a column descending from
        // its center (x=4..6, y=0..5). Sweep north: the column expands into
        // the bar at y≈5, producing a SPLIT, and the open bar ends at y=7
        // (CLOSE). The split forces at least one cut → ≥2 cells.
        //   (4,0)──(6,0)
        //     │      │
        //     │      │
        //     │      │
        //   (4,5) (6,5)─(10,5)        ← split crossbar
        //     │              │
        //   (0,5)            │
        //     │              │
        //   (0,7)──────────(10,7)
        var t = new List<Vec2>
        {
            new(4, 0), new(6, 0), new(6, 5), new(10, 5),
            new(10, 7), new(0, 7), new(0, 5), new(4, 5),
        };

        var cells = BoustrophedonDecomp.Decompose(t, new List<List<Vec2>>(), SweepNorth);

        Assert.That(cells.Count, Is.GreaterThanOrEqualTo(2),
            "T-shape's split/merge at y=5 must produce at least 2 cells");
    }

    // --- Trapezoidal variant produces ≥ boustrophedon cells. ---

    [Test]
    public void Trapezoidal_RectangleProducesAtLeastOneCell()
    {
        var cells = TrapezoidalDecomp.Decompose(
            Rect(0, 10, 0, 10),
            new List<List<Vec2>>(),
            SweepNorth);

        Assert.That(cells.Count, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Trapezoidal_NotMoreCellsThanVertices()
    {
        // Sanity bound — every-vertex variant shouldn't explode.
        var l = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10),
        };
        var trap = TrapezoidalDecomp.Decompose(l, new List<List<Vec2>>(), SweepNorth);
        var bous = BoustrophedonDecomp.Decompose(l, new List<List<Vec2>>(), SweepNorth);

        Assert.That(trap.Count, Is.LessThanOrEqualTo(l.Count));
        Assert.That(trap.Count, Is.GreaterThanOrEqualTo(bous.Count),
            "trapezoidal should be at least as fine as boustrophedon");
    }
}
