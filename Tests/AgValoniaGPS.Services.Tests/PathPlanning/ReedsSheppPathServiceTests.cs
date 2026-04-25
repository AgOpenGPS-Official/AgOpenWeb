// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Services.PathPlanning;

namespace AgValoniaGPS.Services.Tests.PathPlanning;

[TestFixture]
public class ReedsSheppPathServiceTests
{
    private ReedsSheppPathService _rs = null!;
    private const double TurningRadius = 5.0;

    [SetUp]
    public void SetUp()
    {
        _rs = new ReedsSheppPathService(TurningRadius);
    }

    [Test]
    public void IdenticalStartAndGoal_HasZeroLength()
    {
        var pose = new Vec3(0, 0, 0);
        Assert.That(_rs.GetDistance(pose, pose), Is.EqualTo(0.0).Within(1e-6));
    }

    [Test]
    public void StraightAhead_LengthMatchesEuclidean()
    {
        // Goal directly ahead (in heading=0, ahead is +y/+North): no turning needed.
        var start = new Vec3(0, 0, 0);
        var goal = new Vec3(0, 10, 0);
        double rs = _rs.GetDistance(start, goal);
        // Straight-line distance is 10. RS may emit a slightly longer path with
        // tiny arcs, but should be very close to 10.
        Assert.That(rs, Is.EqualTo(10.0).Within(0.5));
    }

    [Test]
    public void StraightBehind_RequiresReverse()
    {
        // Goal behind: vehicle facing north at origin, goal facing north at south.
        // RS can reverse straight back; Dubins cannot.
        var start = new Vec3(0, 0, 0);
        var goal = new Vec3(0, -10, 0);
        double rs = _rs.GetDistance(start, goal);
        Assert.That(rs, Is.EqualTo(10.0).Within(0.5),
            "Straight-back goal should produce ~10m reverse path");
    }

    [Test]
    public void RightAngleTurn_FitsApproximateLength()
    {
        // Standard 90° right turn from facing-north to facing-east at radius 5.
        // Quarter-circle arc of radius 5: length = π/2 * 5 ≈ 7.854.
        var start = new Vec3(0, 0, 0);
        var goal = new Vec3(5, 5, Math.PI / 2);
        double rs = _rs.GetDistance(start, goal);
        Assert.That(rs, Is.EqualTo(Math.PI * 0.5 * TurningRadius).Within(0.5));
    }

    [Test]
    public void UTurn_RsIsNoLongerThanDubins()
    {
        // Tight U-turn: facing north, want to face south, offset 5m east.
        // Dubins requires a wide U; RS can do a 3-point turn fitting tighter.
        var start = new Vec3(0, 0, 0);
        var goal = new Vec3(5, 0, Math.PI);
        double rs = _rs.GetDistance(start, goal);
        var dubins = new DubinsPathService(TurningRadius);
        var dubinsPaths = dubins.GenerateAllPaths(start, goal);
        double dubinsBest = double.MaxValue;
        foreach (var (_, _, length) in dubinsPaths)
            if (length < dubinsBest) dubinsBest = length;

        Assert.That(rs, Is.LessThanOrEqualTo(dubinsBest + 1e-3),
            "RS should be ≤ Dubins for any feasible pose pair");
    }

    [Test]
    public void GetWaypoints_FirstAndLastApproximateStartAndGoal()
    {
        var start = new Vec3(10, 20, 1.5);
        var goal = new Vec3(30, 40, 0.5);
        var waypoints = _rs.GetWaypoints(start, goal, discretization: 0.1);

        Assert.That(waypoints.Count, Is.GreaterThan(0));
        Assert.That(waypoints[0].Easting, Is.EqualTo(start.Easting).Within(1e-6));
        Assert.That(waypoints[0].Northing, Is.EqualTo(start.Northing).Within(1e-6));
        // Goal reached within discretization tolerance.
        Assert.That(waypoints[^1].Easting, Is.EqualTo(goal.Easting).Within(0.5));
        Assert.That(waypoints[^1].Northing, Is.EqualTo(goal.Northing).Within(0.5));
    }

    [Test]
    public void GetShortestPath_TracksReverseSegments()
    {
        // U-turn: should include some reverse motion in 3-point-turn solutions.
        var start = new Vec3(0, 0, 0);
        var goal = new Vec3(2, 0, Math.PI);  // tight U-turn
        var path = _rs.GetShortestPath(start, goal);

        Assert.That(path.Waypoints.Count, Is.GreaterThan(0));
        Assert.That(path.IsReverse.Count, Is.EqualTo(path.Waypoints.Count));
        // Should be at least one reverse waypoint somewhere in the path
        // (Dubins-only would never reverse; RS should pick a tight 3-point turn).
        bool hasReverse = false;
        foreach (var r in path.IsReverse) if (r) { hasReverse = true; break; }
        Assert.That(hasReverse, Is.True,
            "Tight U-turn at radius 5 with offset 2 should require reverse motion");
    }

    [Test]
    public void TurningRadiusValidation()
    {
        Assert.That(() => new ReedsSheppPathService(0), Throws.ArgumentException);
        Assert.That(() => new ReedsSheppPathService(-1), Throws.ArgumentException);
    }
}
