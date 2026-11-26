// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A section's line tested against a small polygon (an obstacle): beside its bounding box the
/// answer comes at once, fully outside, and must be the one the full test gives; touching or
/// over it, the full test still decides.
/// </summary>
[TestFixture]
public class SectionBesideObstacleTests
{
    // A 10 m square obstacle, E 10..20, N 10..20.
    private static BoundaryPolygon Square()
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (10.0, 10.0), (20.0, 10.0), (20.0, 20.0), (10.0, 20.0) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    [TestCase(5.0, 15.0, 0.0, 0.5, TestName = "Heading north, well west of the box")]
    [TestCase(15.0, 7.0, 90.0, 1.0, TestName = "Heading east, south of the box")]
    [TestCase(8.0, 8.0, 45.0, 1.0, TestName = "Heading north-east, off the box's corner")]
    [TestCase(25.0, 25.0, 200.0, 3.0, TestName = "Heading south-south-west, off the far corner")]
    public void A_section_clear_of_the_obstacle_is_fully_outside(double e, double n, double headingDeg, double halfWidth)
    {
        var r = Square().GetSegmentBoundaryStatus(new Vec2(e, n), headingDeg * System.Math.PI / 180, halfWidth);
        Assert.That(r, Is.EqualTo(BoundaryResult.FullyOutside));
    }

    [TestCase(8.8, 15.0, 0.0, 0.5, 0.0, TestName = "Heading north, within 1 m of the box but clear of it (the full test)")]
    [TestCase(10.0, 15.0, 0.0, 1.0, 0.5, TestName = "Heading north, half over the west side")]
    [TestCase(15.0, 10.0, 90.0, 1.0, 0.5, TestName = "Heading east, half over the south side")]
    [TestCase(15.0, 15.0, 0.0, 1.0, 1.0, TestName = "Heading north, over the middle")]
    public void A_section_over_the_obstacle_gets_its_share(double e, double n, double headingDeg, double halfWidth, double inside)
    {
        var r = Square().GetSegmentBoundaryStatus(new Vec2(e, n), headingDeg * System.Math.PI / 180, halfWidth);
        Assert.That(r.InsidePercent, Is.EqualTo(inside).Within(0.01));
    }

    [Test]
    public void The_field_subtracts_only_the_obstacle_under_the_section()
    {
        var field = new Boundary { OuterBoundary = new BoundaryPolygon() };
        foreach (var (e, n) in new[] { (-100.0, -100.0), (100.0, -100.0), (100.0, 100.0), (-100.0, 100.0) })
            field.OuterBoundary.Points.Add(new BoundaryPoint(e, n, 0));
        field.OuterBoundary.UpdateBounds();
        field.InnerBoundaries.Add(Square());

        Assert.Multiple(() =>
        {
            Assert.That(field.GetSegmentBoundaryStatus(new Vec2(-50, 0), 0, 1).InsidePercent, Is.EqualTo(1).Within(0.01), "away from the obstacle");
            Assert.That(field.GetSegmentBoundaryStatus(new Vec2(10, 15), 0, 1).InsidePercent, Is.EqualTo(0.5).Within(0.01), "half over it");
        });
    }
}
