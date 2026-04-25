// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class CriticalPointClassifierTests
{
    private const double SweepNorth = 0.0;

    private static List<Vec2> Rect(double minE, double maxE, double minN, double maxN) => new()
    {
        new Vec2(minE, minN),
        new Vec2(maxE, minN),
        new Vec2(maxE, maxN),
        new Vec2(minE, maxN),
    };

    private static int Count(List<CriticalPoint> cps, CriticalPointType type) =>
        cps.Count(c => c.Type == type);

    [Test]
    public void Rectangle_HasOneOpenAndOneClose()
    {
        var rect = Rect(0, 10, 0, 10);
        var ccw = PolygonOrientation.Ensure(rect, wantCcw: true);
        double adjusted = CriticalPointClassifier.AdjustSweepForNonDegenerateOrder(
            SweepNorth, ccw, new List<List<Vec2>>());

        var cps = CriticalPointClassifier.Classify(ccw, adjusted, isInnerHole: false);

        // Convex rectangle: exactly one global min (OPEN), one global max (CLOSE),
        // and the other two corners are REGULAR after perturbation breaks the tie.
        Assert.That(Count(cps, CriticalPointType.Open), Is.EqualTo(1));
        Assert.That(Count(cps, CriticalPointType.Close), Is.EqualTo(1));
        Assert.That(Count(cps, CriticalPointType.Split), Is.EqualTo(0));
        Assert.That(Count(cps, CriticalPointType.Merge), Is.EqualTo(0));
        Assert.That(Count(cps, CriticalPointType.Regular), Is.EqualTo(2));
    }

    [Test]
    public void RectangleWithInnerHole_HoleProducesOneSplitAndOneMerge()
    {
        var outer = Rect(0, 10, 0, 10);
        var hole = Rect(3, 5, 4, 6);
        var outerCcw = PolygonOrientation.Ensure(outer, wantCcw: true);
        var innerCw = PolygonOrientation.Ensure(hole, wantCcw: false);

        double sweep = CriticalPointClassifier.AdjustSweepForNonDegenerateOrder(
            SweepNorth, outerCcw, new List<List<Vec2>> { innerCw });

        var outerCps = CriticalPointClassifier.Classify(outerCcw, sweep, isInnerHole: false);
        var innerCps = CriticalPointClassifier.Classify(innerCw, sweep, isInnerHole: true);

        // Outer: convex rectangle = 1 OPEN + 1 CLOSE + 2 REGULAR.
        Assert.That(Count(outerCps, CriticalPointType.Open), Is.EqualTo(1));
        Assert.That(Count(outerCps, CriticalPointType.Close), Is.EqualTo(1));

        // Inner rectangle hole: 1 SPLIT (its lowest perturbed vertex starts the
        // split around the obstacle) + 1 MERGE (its highest closes it) +
        // 2 REGULAR (the two horizontal-edge tied vertices).
        Assert.That(Count(innerCps, CriticalPointType.Split), Is.EqualTo(1));
        Assert.That(Count(innerCps, CriticalPointType.Merge), Is.EqualTo(1));
    }

    [Test]
    public void NotchedOuter_NotchProducesSplitAndMerge()
    {
        // Field with a notch in the top: rectangle [0,8]x[0,6] minus rectangle [3,5]x[4,6].
        // CCW order around the notched outer.
        var poly = new List<Vec2>
        {
            new Vec2(0, 0),
            new Vec2(8, 0),
            new Vec2(8, 6),
            new Vec2(5, 6),
            new Vec2(5, 4),
            new Vec2(3, 4),
            new Vec2(3, 6),
            new Vec2(0, 6),
        };
        var ccw = PolygonOrientation.Ensure(poly, wantCcw: true);
        double sweep = CriticalPointClassifier.AdjustSweepForNonDegenerateOrder(
            SweepNorth, ccw, new List<List<Vec2>>());

        var cps = CriticalPointClassifier.Classify(ccw, sweep, isInnerHole: false);

        // Global min at (0,0) → OPEN. Global max at (8,6) → CLOSE.
        // The notch's two outer corners (3,6) and (5,6) tie at y=6 with (8,6) and
        // (0,6) — perturbation gives one of them MERGE; the other side a SPLIT
        // at the bottom inside corner of the notch.
        Assert.That(Count(cps, CriticalPointType.Open), Is.EqualTo(1));
        Assert.That(Count(cps, CriticalPointType.Close), Is.EqualTo(1));
        Assert.That(Count(cps, CriticalPointType.Split), Is.EqualTo(1));
        Assert.That(Count(cps, CriticalPointType.Merge), Is.EqualTo(1));
    }

    [Test]
    public void LShapedOuter_ProducesSplitAndMergeAtElbow()
    {
        // L-shape: rectangle [0,4]x[0,4] minus rectangle [2,4]x[2,4].
        var poly = new List<Vec2>
        {
            new Vec2(0, 0),
            new Vec2(4, 0),
            new Vec2(4, 2),
            new Vec2(2, 2),
            new Vec2(2, 4),
            new Vec2(0, 4),
        };
        var ccw = PolygonOrientation.Ensure(poly, wantCcw: true);
        double sweep = CriticalPointClassifier.AdjustSweepForNonDegenerateOrder(
            SweepNorth, ccw, new List<List<Vec2>>());

        var cps = CriticalPointClassifier.Classify(ccw, sweep, isInnerHole: false);

        // The elbow region produces two adjacent critical events:
        //   - inside corner (2,2): LEFT vertex on outer = SPLIT (closes the bottom
        //     cell as its right wall starts retracting)
        //   - outside corner (4,2): RIGHT vertex on outer = MERGE (closes the top
        //     of the bottom cell's right wall)
        // Phase C's sweep collapses these into a single Reeb edge, but the
        // classifier flags both vertices.
        Assert.That(cps.Count, Is.EqualTo(6));
        Assert.That(Count(cps, CriticalPointType.Open), Is.EqualTo(1));
        Assert.That(Count(cps, CriticalPointType.Close), Is.EqualTo(1));
        Assert.That(Count(cps, CriticalPointType.Split), Is.EqualTo(1));
        Assert.That(Count(cps, CriticalPointType.Merge), Is.EqualTo(1));
    }

    [Test]
    public void EnsureOrientation_FlipsWhenNeeded()
    {
        var cw = new List<Vec2>
        {
            new Vec2(0, 0),
            new Vec2(0, 10),
            new Vec2(10, 10),
            new Vec2(10, 0),
        };
        Assert.That(PolygonOrientation.IsCcw(cw), Is.False);

        var ccw = PolygonOrientation.Ensure(cw, wantCcw: true);
        Assert.That(PolygonOrientation.IsCcw(ccw), Is.True);
        Assert.That(ccw.Count, Is.EqualTo(4));
    }
}
