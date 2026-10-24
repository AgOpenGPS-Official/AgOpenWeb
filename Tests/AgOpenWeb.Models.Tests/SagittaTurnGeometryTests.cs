// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.YouTurn;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

[TestFixture]
public class SagittaTurnGeometryTests
{
    private const double R = 5.0;

    private static double MaxStep(IReadOnlyList<Vec3> path)
    {
        double max = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double d = GeometryMath.Distance(path[i - 1], path[i]);
            if (d > max) max = d;
        }
        return max;
    }

    private static List<Vec3> Keyhole(double lateral, bool right = true, double r = R) =>
        SagittaTurnGeometry.BuildKeyhole(new Vec3(0, 0, 0), 0.0, right, r, lateral);

    [Test]
    public void FullWidth_IsAPlainSemicircle()
    {
        // Heading north, turning right onto a pass 2R over: no counter-arcs, depth R.
        var path = Keyhole(2 * R);
        Assert.Multiple(() =>
        {
            Assert.That(path[^1].Easting, Is.EqualTo(2 * R).Within(0.05));
            Assert.That(path[^1].Northing, Is.EqualTo(0.0).Within(0.05));
            Assert.That(path.Min(p => p.Easting), Is.GreaterThanOrEqualTo(-0.01), "no swing the other way");
            Assert.That(path.Max(p => p.Northing), Is.EqualTo(R).Within(0.05));
        });
    }

    [TestCase(0.5)]
    [TestCase(3.0)]
    [TestCase(5.0)]
    [TestCase(8.0)]
    [TestCase(10.0)]
    public void LandsOnTheTargetPass_LevelWithTheStart_HeadingBack(double lateral)
    {
        var end = Keyhole(lateral)[^1];
        Assert.Multiple(() =>
        {
            Assert.That(end.Easting, Is.EqualTo(lateral).Within(0.05));
            Assert.That(end.Northing, Is.EqualTo(0.0).Within(0.05));
            Assert.That(Math.Abs(Math.IEEERemainder(end.Heading - Math.PI, 2 * Math.PI)), Is.LessThan(0.02));
        });
    }

    [TestCase(3.0)]
    [TestCase(6.0)]
    public void IsSymmetric_AboutTheMidline(double lateral)
    {
        // #418: the loop is centred between the passes, so it swings as far past the
        // start pass as past the target.
        var path = Keyhole(lateral);
        double mid = lateral / 2;
        Assert.That(mid - path.Min(p => p.Easting), Is.EqualTo(path.Max(p => p.Easting) - mid).Within(0.05));
    }

    [Test]
    public void Report418_Rig_IsAbout20mDeep()
    {
        // 6 m passes, 8 m radius: the one-sided turn was 22.8 m deep, 10 m to the far side.
        var path = Keyhole(6, r: 8);
        Assert.Multiple(() =>
        {
            Assert.That(path.Max(p => p.Northing), Is.EqualTo(19.62).Within(0.05));
            Assert.That(SagittaTurnGeometry.KeyholeDepth(8, 6), Is.EqualTo(19.62).Within(0.01));
            Assert.That(path.Min(p => p.Easting), Is.EqualTo(-5).Within(0.05));
            Assert.That(path.Max(p => p.Easting), Is.EqualTo(11).Within(0.05));
        });
    }

    [Test]
    public void StartsTheOppositeWay()
    {
        // A right turn onto a near pass first bends left (the counter-arc).
        var path = Keyhole(R);
        Assert.That(path[1].Easting, Is.LessThan(0.0));
    }

    [Test]
    public void HeadingFollowsThePath()
    {
        var path = Keyhole(4);
        for (int i = 1; i < path.Count - 1; i++)
        {
            double h = Math.Atan2(path[i + 1].Easting - path[i - 1].Easting, path[i + 1].Northing - path[i - 1].Northing);
            Assert.That(Math.Abs(Math.IEEERemainder(path[i].Heading - h, 2 * Math.PI)), Is.LessThan(0.12), $"point {i}");
        }
    }

    [Test]
    public void LeftAndRight_AreMirrored()
    {
        Assert.That(Keyhole(2 * R, right: true)[^1].Easting, Is.EqualTo(+2 * R).Within(0.05));
        Assert.That(Keyhole(2 * R, right: false)[^1].Easting, Is.EqualTo(-2 * R).Within(0.05));
        Assert.That(Keyhole(R, right: false)[^1].Easting, Is.EqualTo(-R).Within(0.05));
    }

    [Test]
    public void Points_AreFinelySpaced_NoLargeGaps()
    {
        // Segment sizing is radius * 0.1, so steps stay well under ~0.6 m for R=5.
        Assert.That(MaxStep(Keyhole(R)), Is.LessThan(R * 0.15));
    }

    [Test]
    public void ZeroRadius_DoesNotThrow_ReturnsStartOnly()
    {
        var path = SagittaTurnGeometry.BuildKeyhole(new Vec3(1, 2, 0), 0.0, true, 0.0, 3.0);
        Assert.That(path.Count, Is.EqualTo(1));
    }
}
