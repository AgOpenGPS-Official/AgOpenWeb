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
}
