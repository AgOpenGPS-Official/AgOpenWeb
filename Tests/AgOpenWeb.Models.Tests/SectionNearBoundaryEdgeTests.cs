// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Headland;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A section's line near the field's edge: only a boundary crossing on the section itself
/// changes how much of it is inside. Crossings up to 1 m beyond its ends were counted, and the
/// walk along the section starts at its left end, so an edge just past that end turned a section
/// wholly in the field to 0 % inside: with "Off outside boundary" on, the outer section stayed
/// shut along the boundary on the first lap (path job scenarios P1, EDGE-NOT-SPRAYED).
/// </summary>
[TestFixture]
public class SectionNearBoundaryEdgeTests
{
    // A 100 m square field, E 0..100, N 0..100.
    private static BoundaryPolygon Field()
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 100.0), (0.0, 100.0) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    // Heading north: the section's left end is west.
    [TestCase(1.0, 1.0, TestName = "Its left end on the boundary: all in")]
    [TestCase(1.5, 1.0, TestName = "Its left end 0.5 m in: all in")]
    [TestCase(2.0, 1.0, TestName = "Its left end 1 m in: all in")]
    [TestCase(0.5, 0.75, TestName = "Its left end 0.5 m out: three quarters in")]
    public void Heading_north_along_the_west_edge(double centreE, double expectedInside)
    {
        var r = Field().GetSegmentBoundaryStatus(new Vec2(centreE, 50), 0, 1.0);
        Assert.That(r.InsidePercent, Is.EqualTo(expectedInside).Within(1e-6));
    }

    [Test]
    public void Heading_south_along_the_west_edge_its_right_end_1_m_in()
    {
        var r = Field().GetSegmentBoundaryStatus(new Vec2(2.0, 50), System.Math.PI, 1.0);
        Assert.That(r.InsidePercent, Is.EqualTo(1.0).Within(1e-6));
    }
}
