// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.PathPlanning;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The recorded route's Pure Pursuit, now run by the GPS pipeline so AutoSteer steers along a
/// played route (P7). A bicycle model with the simulator's sign (+ steer turns right) follows it.
/// </summary>
[TestFixture]
public class RecordedPathFollowerTests
{
    // A recorded route: 60 m north, then a 30 m radius bend to the east, points every 0.5 m.
    private static List<Vec3> Route()
    {
        var pts = new List<Vec3>();
        for (double y = 0; y < 60; y += 0.5) pts.Add(new Vec3(0, y, 0));
        for (double a = 0; a <= Math.PI / 2; a += 0.5 / 30) pts.Add(new Vec3(30 - 30 * Math.Cos(a), 60 + 30 * Math.Sin(a), a));
        return pts;
    }

    private static double DistanceTo(List<Vec3> path, double e, double n)
    {
        double best = double.MaxValue;
        foreach (var p in path) best = Math.Min(best, Math.Sqrt((p.Easting - e) * (p.Easting - e) + (p.Northing - n) * (p.Northing - n)));
        return best;
    }

    [Test]
    public void A_tractor_one_metre_off_the_route_is_steered_onto_it_and_along_the_bend()
    {
        var route = Route();
        var f = new RecordedPathFollower(route, 0, approach: false);
        double e = 1.0, n = 0, h = 0, wheelbase = 3.0, step = 8 / 3.6 * 0.1; // 8 km/h, 10 Hz
        double worstAfterSettling = 0;
        int fixes = 0;
        while (f.Follow(e, n, h, wheelbase, 35) is { } s && fixes++ < 2000)
        {
            if (fixes == 1) Assert.That(s.SteerAngleDegrees, Is.LessThan(0), "1 m right of the route, it steers left");
            h += step * Math.Tan(s.SteerAngleDegrees * Math.PI / 180) / wheelbase;
            e += step * Math.Sin(h); n += step * Math.Cos(h);
            if (n > 20) worstAfterSettling = Math.Max(worstAfterSettling, DistanceTo(route, e, n));
        }
        Assert.That(n, Is.GreaterThan(85), "it drove the route to its end");
        Assert.That(worstAfterSettling, Is.LessThan(0.3), "on the route, along the straight and the bend");
        Assert.That(f.Follow(e, n, h, wheelbase, 35), Is.Null, "nothing to steer to past the end");
    }

    [Test]
    public void The_cross_track_error_is_positive_right_of_the_route()
    {
        var f = new RecordedPathFollower(Route(), 0, approach: false);
        Assert.That(f.Follow(1.0, 5, 0, 3, 35)!.Value.CrossTrackError, Is.EqualTo(1.0).Within(1e-9));
        f = new RecordedPathFollower(Route(), 0, approach: false);
        Assert.That(f.Follow(-0.5, 5, 0, 3, 35)!.Value.CrossTrackError, Is.EqualTo(-0.5).Within(1e-9));
    }

    [Test]
    public void The_approach_hands_over_to_the_route_without_a_cycle_with_no_line()
    {
        // The approach: 10 m north from (0,-10) to the route's start at (0,0).
        var approach = new List<Vec3>();
        for (double y = -10; y <= 0; y += 0.5) approach.Add(new Vec3(0, y, 0));
        var g = new RecordedRouteGuide(approach, Route(), 0);
        double e = 0, n = -10, h = 0, step = 8 / 3.6 * 0.1;
        for (int fix = 0; n < 40; fix++)
        {
            var s = g.Follow(e, n, h, 3, 35);
            Assert.That(s, Is.Not.Null, $"a line at every fix (fix {fix}, {n:0.0} m north)");
            h += step * Math.Tan(s!.Value.SteerAngleDegrees * Math.PI / 180) / 3;
            e += step * Math.Sin(h); n += step * Math.Cos(h);
        }
    }
}
