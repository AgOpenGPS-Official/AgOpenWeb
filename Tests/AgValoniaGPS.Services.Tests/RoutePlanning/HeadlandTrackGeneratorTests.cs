// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class HeadlandTrackGeneratorTests
{
    private static List<Vec2> Square(double minE, double maxE, double minN, double maxN)
        => new() { new(minE, minN), new(maxE, minN), new(maxE, maxN), new(minE, maxN) };

    [Test]
    public void OuterOnly_OnePass_ProducesOneInwardLoop()
    {
        var outer = Square(0, 100, 0, 100);
        var loops = new HeadlandTrackGenerator().Generate(outer, new List<List<Vec2>>(), opWidth: 10, passes: 1);

        Assert.That(loops.Count, Is.EqualTo(1));
        Assert.That(loops[0].Kind, Is.EqualTo(HeadlandLoopKind.Outer));
        Assert.That(loops[0].PassIndex, Is.EqualTo(0));
        // Pass 0 sits 5m inward (half opWidth) from the outer 100x100 — perimeter ≈ 4·90 = 360.
        Assert.That(loops[0].Perimeter, Is.EqualTo(360.0).Within(2.0));
    }

    [Test]
    public void OuterPlusInner_TwoPasses_ProducesFourLoops()
    {
        var outer = Square(0, 100, 0, 100);
        var pond = Square(40, 60, 40, 60);
        var loops = new HeadlandTrackGenerator().Generate(outer, new List<List<Vec2>> { pond }, opWidth: 10, passes: 2);

        // 2 outer + 2 inner.
        Assert.That(loops.Count, Is.EqualTo(4));
        int outerCount = 0, innerCount = 0;
        foreach (var l in loops)
        {
            if (l.Kind == HeadlandLoopKind.Outer) outerCount++;
            else innerCount++;
        }
        Assert.That(outerCount, Is.EqualTo(2));
        Assert.That(innerCount, Is.EqualTo(2));
    }

    [Test]
    public void Project_PointOutsideLoop_ReturnsClosestPerimeterPoint()
    {
        // Square loop from (10,10) to (90,90).
        var loop = new HeadlandLoop(Square(10, 90, 10, 90), HeadlandLoopKind.Outer, 0, 0);
        // A point at (50, 200) (well above the top edge) projects onto the top edge.
        var p = loop.Project(new Vec2(50, 200));
        Assert.That(p.Position.Easting, Is.EqualTo(50).Within(0.01));
        Assert.That(p.Position.Northing, Is.EqualTo(90).Within(0.01));
    }

    [Test]
    public void Walk_HalfPerimeter_ProducesDenseSamples()
    {
        // 100m perimeter square: walk from one mid-edge to the opposite mid-edge.
        var loop = new HeadlandLoop(Square(0, 25, 0, 25), HeadlandLoopKind.Outer, 0, 0);
        var from = loop.Project(new Vec2(12.5, -10));   // projects to bottom mid-edge (12.5, 0)
        var to = loop.Project(new Vec2(12.5, 100));     // projects to top mid-edge (12.5, 25)

        var fwd = loop.Walk(from, to, forward: true, maxStep: 1.0);
        var bwd = loop.Walk(from, to, forward: false, maxStep: 1.0);

        // Both directions should reach `to` (within a small tolerance).
        Assert.That(fwd[^1].Easting, Is.EqualTo(12.5).Within(0.01));
        Assert.That(fwd[^1].Northing, Is.EqualTo(25).Within(0.01));
        Assert.That(bwd[^1].Easting, Is.EqualTo(12.5).Within(0.01));
        Assert.That(bwd[^1].Northing, Is.EqualTo(25).Within(0.01));

        // Both walks should be roughly half the perimeter in length.
        // (Square 25x25 perimeter = 100; mid-bottom to mid-top via either side = 50.)
        Assert.That(fwd.Count, Is.GreaterThan(40));   // ~50 samples at 1m maxStep
        Assert.That(bwd.Count, Is.GreaterThan(40));
    }

    [Test]
    public void Walk_DegenerateSamePoint_ReturnsAtMostTwo()
    {
        var loop = new HeadlandLoop(Square(0, 10, 0, 10), HeadlandLoopKind.Outer, 0, 0);
        var p = loop.Project(new Vec2(5, -1));
        var walk = loop.Walk(p, p, forward: true, maxStep: 1.0);
        Assert.That(walk.Count, Is.LessThanOrEqualTo(2));
    }

    [Test]
    public void Project_TangentIsUnitLength()
    {
        var loop = new HeadlandLoop(Square(0, 10, 0, 10), HeadlandLoopKind.Outer, 0, 0);
        var p = loop.Project(new Vec2(5, -1));
        double mag = Math.Sqrt(p.Tangent.Easting * p.Tangent.Easting + p.Tangent.Northing * p.Tangent.Northing);
        Assert.That(mag, Is.EqualTo(1.0).Within(1e-6));
    }

    [Test]
    public void ForwardArc_RoundTripIsPerimeter()
    {
        var loop = new HeadlandLoop(Square(0, 10, 0, 10), HeadlandLoopKind.Outer, 0, 0);
        var a = loop.Project(new Vec2(5, -1));
        var b = loop.Project(new Vec2(5, 11));
        Assert.That(loop.ForwardArc(a, b) + loop.ForwardArc(b, a),
            Is.EqualTo(loop.Perimeter).Within(1e-6));
    }
}
