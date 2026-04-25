// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Services.PathPlanning;

namespace AgValoniaGPS.Services.Tests.PathPlanning;

[TestFixture]
public class DubinsCcPathServiceTests
{
    private const double TurningRadius = 5.0;
    private const double BlendDistance = 0.5;

    [Test]
    public void TurningRadiusValidation()
    {
        Assert.That(() => new DubinsCcPathService(0), Throws.ArgumentException);
        Assert.That(() => new DubinsCcPathService(-1), Throws.ArgumentException);
    }

    [Test]
    public void BlendDistanceValidation()
    {
        Assert.That(() => new DubinsCcPathService(TurningRadius, -0.1), Throws.ArgumentException);
    }

    [Test]
    public void EndpointsAreApproximatelyPreserved()
    {
        var cc = new DubinsCcPathService(TurningRadius, BlendDistance);
        var start = new Vec3(0, 0, 0);
        // 90° right turn requiring a real Dubins path with 3 distinct segments.
        var goal = new Vec3(20, 20, Math.PI / 2);
        var p = cc.GeneratePath(start, goal);

        Assert.That(p.Waypoints.Count, Is.GreaterThan(2));
        Assert.That(p.Waypoints[0].Easting, Is.EqualTo(start.Easting).Within(1e-6));
        Assert.That(p.Waypoints[0].Northing, Is.EqualTo(start.Northing).Within(1e-6));
        Assert.That(p.Waypoints[^1].Easting, Is.EqualTo(goal.Easting).Within(1e-6));
        Assert.That(p.Waypoints[^1].Northing, Is.EqualTo(goal.Northing).Within(1e-6));
    }

    [Test]
    public void BlendDistanceZero_MatchesPlainDubinsLength()
    {
        var cc = new DubinsCcPathService(TurningRadius, blendDistance: 0.0);
        var dubins = new DubinsPathService(TurningRadius);

        var start = new Vec3(0, 0, 0);
        var goal = new Vec3(20, 20, Math.PI / 2);

        var ccPath = cc.GeneratePath(start, goal);
        var pd = dubins.GetBestPathData(start, goal);
        Assert.That(pd, Is.Not.Null);

        Assert.That(ccPath.Smoothed, Is.False);
        Assert.That(ccPath.Length, Is.EqualTo(pd!.TotalLength).Within(1e-9));
        Assert.That(ccPath.Type, Is.EqualTo(pd.PathType.ToString()));
    }

    [Test]
    public void Length_WithinFivePercentOfPlainDubins()
    {
        var cc = new DubinsCcPathService(TurningRadius, BlendDistance);
        var dubins = new DubinsPathService(TurningRadius);

        // Cover a few headland-style geometries.
        Vec3[] starts =
        {
            new(0, 0, 0),
            new(0, 0, 0),
            new(0, 0, Math.PI / 4),
        };
        Vec3[] goals =
        {
            new(20, 20, Math.PI / 2),
            new(15, -10, Math.PI),
            new(25, 5, Math.PI),
        };

        for (int i = 0; i < starts.Length; i++)
        {
            var pd = dubins.GetBestPathData(starts[i], goals[i]);
            Assert.That(pd, Is.Not.Null, $"case {i}: plain Dubins must exist");
            var ccPath = cc.GeneratePath(starts[i], goals[i]);

            double rel = Math.Abs(ccPath.Length - pd!.TotalLength) / pd.TotalLength;
            Assert.That(rel, Is.LessThan(0.05),
                $"case {i}: CC length {ccPath.Length:F3} vs plain {pd.TotalLength:F3} differs by {rel:P2}");
        }
    }

    [Test]
    public void HeadingChangePerStep_IsBoundedSmallEverywhere()
    {
        // Blended path should have small per-step heading deltas: no curvature
        // jumps, no overshoot artifacts, no near-π wrap-arounds.
        var cc = new DubinsCcPathService(TurningRadius, BlendDistance);

        var start = new Vec3(0, 0, 0);
        var goal = new Vec3(20, 20, Math.PI / 2);

        var path = cc.GeneratePath(start, goal);
        double maxDelta = MaxConsecutiveHeadingDelta(path.Waypoints);

        // A raw arc step at 0.05 m / 5 m radius turns 0.01 rad. Bezier samples
        // across a curvature blend can be modestly larger but well under 0.05.
        Assert.That(maxDelta, Is.LessThan(0.05),
            "Blended path max heading delta should be small per step");
    }

    private static double MaxConsecutiveHeadingDelta(System.Collections.Generic.List<Vec3> path)
    {
        const double TwoPi = 2.0 * Math.PI;
        double max = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double d = path[i].Heading - path[i - 1].Heading;
            // Wrap to (-π, π].
            d = (d + TwoPi) % TwoPi;
            if (d > Math.PI) d -= TwoPi;
            d = Math.Abs(d);
            if (d > max) max = d;
        }
        return max;
    }
}
