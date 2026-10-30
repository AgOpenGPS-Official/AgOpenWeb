// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// Water pivot tracks (AgOpenGPS TrackMode.waterPivot): a circle round the centre, and passes
/// that are circles about the same centre, half a width either side of it.
/// </summary>
[TestFixture]
public class WaterPivotTests
{
    private static double Dist(Vec3 a, Vec3 b) => Math.Sqrt(Math.Pow(a.Easting - b.Easting, 2) + Math.Pow(a.Northing - b.Northing, 2));

    [Test]
    public void FitCircle_finds_the_circle_through_part_of_an_arc()
    {
        var center = new Vec3(120, -40, 0);
        var rng = new Random(64);
        var arc = Enumerable.Range(0, 40)
            .Select(i => 0.3 + i * 0.02) // about 45 degrees of a 300 m pivot
            .Select(a => new Vec3(center.Easting + 300 * Math.Sin(a) + (rng.NextDouble() - 0.5) * 0.04,
                                  center.Northing + 300 * Math.Cos(a) + (rng.NextDouble() - 0.5) * 0.04, 0))
            .ToList();

        var fit = CurveProcessing.FitCircle(arc);

        Assert.That(fit, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(Dist(fit!.Value.Center, center), Is.LessThan(0.5));
            Assert.That(fit.Value.Radius, Is.EqualTo(300).Within(0.5));
            Assert.That(fit.Value.Rms, Is.LessThan(0.05));
        });
    }

    [Test]
    public void FitCircle_refuses_points_in_a_line()
    {
        var line = Enumerable.Range(0, 20).Select(i => new Vec3(i * 2.0, i * 1.0, 0)).ToList();
        Assert.That(CurveProcessing.FitCircle(line), Is.Null);
    }

    [Test]
    public void A_pivot_track_is_a_closed_clockwise_circle_with_a_point_every_metre()
    {
        var c = new Vec3(10, 20, 0);
        var t = TrackModel.FromPivot("P", c, 150);
        var (center, radius) = t.PivotCircle();

        Assert.Multiple(() =>
        {
            Assert.That(t.IsPivot && t.IsClosed);
            Assert.That(t.Points[0].Easting, Is.EqualTo(t.Points[^1].Easting));
            Assert.That(t.Points.All(p => Math.Abs(Dist(p, c) - 150) < 1e-9));
            Assert.That(t.Points.Zip(t.Points.Skip(1), Dist).Max(), Is.LessThanOrEqualTo(1.0 + 1e-9));
            // Clockwise from north: the second point is east of the first.
            Assert.That(t.Points[1].Easting, Is.GreaterThan(t.Points[0].Easting));
            Assert.That(Dist(center, c), Is.LessThan(1e-6));
            Assert.That(radius, Is.EqualTo(150).Within(1e-3));
        });
    }

    // Right of travel on the clockwise circle is inward: pass offsets shrink the circle, and
    // past the centre it is the circle on the other side, as AgOpenGPS's negative radius.
    [TestCase(9.0, 141.0)]
    [TestCase(-9.0, 159.0)]
    [TestCase(160.0, 10.0)]
    public void A_pivot_pass_is_a_circle_about_the_same_centre(double offset, double radius)
    {
        var c = new Vec3(10, 20, 0);
        var pass = CurveProcessing.PivotPass(c, 150, offset);
        Assert.That(pass.All(p => Math.Abs(Dist(p, c) - radius) < 1e-9));
    }
}
