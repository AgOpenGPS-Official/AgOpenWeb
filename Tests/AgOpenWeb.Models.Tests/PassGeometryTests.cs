// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>AgOpenGPS's pass positions: pass n is (n + ½) × width + nudge right of the reference.</summary>
[TestFixture]
public class PassGeometryTests
{
    private static readonly Track.Track Line = Track.Track.FromABLine("AB", new Vec3(0, 0, 0), new Vec3(0, 100, 0));
    private static readonly Track.Track Follow = new() { NoPassOffset = true };

    [TestCase(0, 3.0)]
    [TestCase(1, 9.0)]
    [TestCase(-1, -3.0)]
    [TestCase(-2, -9.0)]
    public void PassesLieHalfAWidthOffTheReference(int pass, double expected) =>
        Assert.That(PassGeometry.Offset(Line, pass, 6), Is.EqualTo(expected).Within(1e-9));

    [Test]
    public void NudgeAdds() => Assert.That(PassGeometry.Offset(Line, 0, 6, 0.4), Is.EqualTo(3.4).Within(1e-9));

    [TestCase(0.1, 0)]
    [TestCase(5.9, 0)]
    [TestCase(6.1, 1)]
    [TestCase(-0.1, -1)]
    [TestCase(-5.9, -1)]
    [TestCase(-6.1, -2)]
    public void Nearest_IsThePassWhoseStripHoldsThePoint(double dist, int expected) =>
        Assert.That(PassGeometry.Nearest(Line, dist, 6), Is.EqualTo(expected));

    [Test]
    public void BoundaryFollow_IsDrivenOnTheLine()
    {
        Assert.That(PassGeometry.Offset(Follow, 0, 6), Is.EqualTo(0));
        Assert.That(PassGeometry.Offset(Follow, 2, 6), Is.EqualTo(12));
        Assert.That(PassGeometry.Nearest(Follow, 2.9, 6), Is.EqualTo(0));
    }

    // #446: a 0.8 m tool offset (tool right of the tractor), 5.8 m between passes. The tractor
    // steers beside the pass so the tool runs on it, on the other side coming back.
    [Test]
    public void ToolShift_TurnsWithTheDirectionOfTravel()
    {
        Assert.That(PassGeometry.ToolShift(0.8, isHeadingSameWay: true), Is.EqualTo(0.8));
        Assert.That(PassGeometry.ToolShift(0.8, isHeadingSameWay: false), Is.EqualTo(-0.8));
    }

    [Test]
    public void WithAToolOffset_TheToolRunsOnEveryPass_BothWays()
    {
        const double w = 5.8, offset = 0.8;
        for (int pass = -2; pass <= 2; pass++)
        {
            bool sameWay = pass % 2 == 0; // back and forth
            double shift = PassGeometry.ToolShift(offset, sameWay);
            double tractor = PassGeometry.Offset(Line, pass, w, 0, shift);
            Assert.That(tractor + shift, Is.EqualTo(PassGeometry.Offset(Line, pass, w)).Within(1e-9),
                $"pass {pass}: the tool on the pass centre");
        }
        // Neighbouring tool strips are the spacing apart, not 4.2 / 7.4 m as before.
        double Tool(int pass) => PassGeometry.Offset(Line, pass, w, 0, PassGeometry.ToolShift(offset, pass % 2 == 0))
            + PassGeometry.ToolShift(offset, pass % 2 == 0);
        Assert.That(Tool(1) - Tool(0), Is.EqualTo(w).Within(1e-9));
        Assert.That(Tool(2) - Tool(1), Is.EqualTo(w).Within(1e-9));
    }

    [Test]
    public void WithAToolOffset_TheNearestPassIsTheToolsOne()
    {
        // Tractor 2.4 m right of the reference, tool 0.8 m right of it at 3.2 m: pass 0 (0..6 m
        // strip with w = 6). Coming back the tool is at 1.6 m: still pass 0, the tractor at 2.4.
        Assert.That(PassGeometry.Nearest(Line, 2.4, 6, 0, 0.8), Is.EqualTo(0));
        // Tractor at 5.5 m: its tool at 6.3 m is on pass 1 going one way, at 4.7 m on pass 0 the other.
        Assert.That(PassGeometry.Nearest(Line, 5.5, 6, 0, PassGeometry.ToolShift(0.8, true)), Is.EqualTo(1));
        Assert.That(PassGeometry.Nearest(Line, 5.5, 6, 0, PassGeometry.ToolShift(0.8, false)), Is.EqualTo(0));
    }
}
