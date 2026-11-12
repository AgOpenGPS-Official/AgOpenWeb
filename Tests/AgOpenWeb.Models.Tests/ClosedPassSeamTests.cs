// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A closed track's pass at its seam. The loop's last point repeats its first, and the
/// offset moved that point twice: once along the last stretch's normal, once along the first's.
/// On the inside of the bend there the second copy lies behind the first, so the pass ran past the
/// seam and back (0.2 m on a 26 m lap): its closing stretch pointed backwards, and a tractor a little
/// off the line at the seam was steered to a goal behind it and turned round.
/// </summary>
[TestFixture]
public class ClosedPassSeamTests
{
    // A round field of 30 m radius, its fence clockwise from the west point: right of travel is in.
    private static List<Vec3> Round()
    {
        const int n = 120;
        return CurveProcessing.DensifyLoop(Enumerable.Range(0, n).Select(i =>
        {
            double a = 2 * Math.PI * i / n;
            return new Vec3(-30 * Math.Cos(a), 30 * Math.Sin(a), 0);
        }).ToList());
    }

    private static readonly Vec3[] Square = { new(-50, -50, 0), new(-50, 50, 0), new(50, 50, 0), new(50, -50, 0) };

    // The largest change of heading from one stretch of the loop to the next, the seam included
    // (degrees). A stretch that doubles back turns by about 180°.
    private static double SharpestTurnRound(IReadOnlyList<Vec3> pass)
    {
        var pts = pass.ToList();
        Assert.That(pts[0].Easting, Is.EqualTo(pts[^1].Easting).Within(1e-9), "closed at the seam");
        Assert.That(pts[0].Northing, Is.EqualTo(pts[^1].Northing).Within(1e-9), "closed at the seam");
        pts.Add(pts[1]);
        double worst = 0;
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            double h1 = Math.Atan2(pts[i].Easting - pts[i - 1].Easting, pts[i].Northing - pts[i - 1].Northing);
            double h2 = Math.Atan2(pts[i + 1].Easting - pts[i].Easting, pts[i + 1].Northing - pts[i].Northing);
            worst = Math.Max(worst, Math.Abs(Math.IEEERemainder(h2 - h1, 2 * Math.PI)) * 180 / Math.PI);
        }
        return worst;
    }

    [Test]
    public void An_inner_lap_of_a_round_field_does_not_double_back_at_the_seam([Values(4.0, 12.0, 20.0)] double d)
    {
        var pass = CurveProcessing.CreateOffsetCurveWithInfo(Round(), d, closed: true).Points;
        Assert.That(SharpestTurnRound(pass), Is.LessThan(10), "a lap of 10 m or more turns a few degrees a metre");
    }

    [Test]
    public void An_outer_lap_of_a_round_field_does_not_double_back_at_the_seam([Values(4.0, 12.0)] double d)
    {
        var pass = CurveProcessing.CreateOffsetCurveWithInfo(Round(), -d, closed: true).Points;
        Assert.That(SharpestTurnRound(pass), Is.LessThan(10));
    }

    [Test]
    public void The_pass_steered_does_not_double_back_at_the_seam([Values(4.0, 12.0)] double d)
    {
        var pass = CurveProcessing.CurvePass(Round(), d, closed: true, minTurnRadius: 8).Points;
        Assert.That(SharpestTurnRound(pass), Is.LessThan(10));
    }

    // The square's seam is on a corner: the offset leaves out the points round the inside of it.
    [Test]
    public void An_inner_lap_of_a_square_field_does_not_double_back_at_its_seam_corner([Values(3.0, 9.0, 21.0)] double d)
    {
        var pass = CurveProcessing.CreateOffsetCurveWithInfo(CurveProcessing.DensifyLoop(Square), d, closed: true).Points;
        Assert.That(SharpestTurnRound(pass), Is.LessThan(120), "no stretch doubles back");
    }

    [Test]
    public void Every_point_of_an_inner_lap_keeps_its_distance_from_the_fence([Values(4.0, 12.0)] double d)
    {
        var pass = CurveProcessing.CreateOffsetCurveWithInfo(Round(), d, closed: true).Points;
        foreach (var p in pass)
            Assert.That(Math.Sqrt(p.Easting * p.Easting + p.Northing * p.Northing), Is.EqualTo(30 - d).Within(0.05));
    }
}
