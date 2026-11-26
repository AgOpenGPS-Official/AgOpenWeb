// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A curve drawn between two taps on the fence follows the fence the shorter way round, as
/// AgOpenGPS's FormABDraw makes a curve from two picks on the boundary.
/// </summary>
[TestFixture]
public class FencePathTests
{
    // A 100 x 60 m field, clockwise from the south-west corner.
    private static readonly List<Vec2> Ring = new() { new(0, 0), new(0, 60), new(100, 60), new(100, 0) };

    private static double ToFence(Vec3 p) => FencePath.Project(Ring, new Vec2(p.Easting, p.Northing)).Distance;

    [Test]
    public void Run_round_a_corner_takes_the_fence_the_shorter_way()
    {
        // West side to north side: round the north-west corner (40 m), not the other three sides (280 m).
        var run = FencePath.ShorterRun(Ring, new Vec2(0, 40), new Vec2(20, 60));

        Assert.Multiple(() =>
        {
            Assert.That(run.All(p => ToFence(p) < 1e-9), "on the fence");
            Assert.That(run.Any(p => p.Easting == 0 && p.Northing == 60), "through the corner");
            Assert.That(run.Zip(run.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.Easting - a.Easting, 2) + Math.Pow(b.Northing - a.Northing, 2))).Max(),
                Is.LessThanOrEqualTo(1.0 + 1e-9), "a point every metre");
            Assert.That(run[0].Northing, Is.EqualTo(40).Within(1e-9));
            Assert.That(run[^1].Easting, Is.EqualTo(20).Within(1e-9));
        });
    }

    [Test]
    public void Run_the_other_way_round_when_that_is_shorter()
    {
        // North side near the east end to the east side: round the north-east corner, against the ring order.
        var run = FencePath.ShorterRun(Ring, new Vec2(100, 30), new Vec2(80, 60));
        Assert.That(run.Any(p => p.Easting == 100 && p.Northing == 60), "through the north-east corner");
        Assert.That(run.All(p => p.Easting >= 80 - 1e-9), "not round the west end");
    }

    [Test]
    public void Run_along_one_side_stays_on_it()
    {
        var run = FencePath.ShorterRun(Ring, new Vec2(10, 60), new Vec2(70, 60));
        Assert.That(run.All(p => Math.Abs(p.Northing - 60) < 1e-9 && p.Easting >= 10 - 1e-9 && p.Easting <= 70 + 1e-9));
    }
}
