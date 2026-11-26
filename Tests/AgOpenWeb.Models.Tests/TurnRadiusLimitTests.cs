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
/// A pass's corners are rounded to the U-turn radius, what the rig can turn: an inner pass of a
/// boundary curve keeps the field's sharp corner, which the tractor can't drive, and pure pursuit
/// cut it by up to 2 m at 15 km/h. Straight stretches and gentle curves are left alone.
/// </summary>
[TestFixture]
public class TurnRadiusLimitTests
{
    private static readonly Vec3[] Square = { new(-50, -50, 0), new(-50, 50, 0), new(50, 50, 0), new(50, -50, 0) };

    // The tightest bend along a line, as the radius of the circle through points 2 m either side.
    private static double TightestRadius(IReadOnlyList<Vec3> pts, bool closed)
    {
        var r = CurveProcessing.ResampleUniform(pts, 1.0);
        int n = closed ? r.Count - 1 : r.Count;
        double min = double.PositiveInfinity;
        for (int i = closed ? 0 : 2; i < (closed ? n : n - 2); i++)
        {
            var a = r[((i - 2) % n + n) % n]; var b = r[i]; var c = r[(i + 2) % n];
            double ab = Dist(a, b), bc = Dist(b, c), ca = Dist(c, a);
            double area2 = Math.Abs((b.Easting - a.Easting) * (c.Northing - a.Northing) - (b.Northing - a.Northing) * (c.Easting - a.Easting));
            if (area2 > 1e-9) min = Math.Min(min, ab * bc * ca / (2 * area2));
        }
        return min;
    }

    private static double Dist(Vec3 a, Vec3 b) => Math.Sqrt(Math.Pow(a.Easting - b.Easting, 2) + Math.Pow(a.Northing - b.Northing, 2));

    [Test]
    public void Inner_pass_of_a_boundary_curve_gets_drivable_corners([Values(3.0, 9.0, 27.0)] double d)
    {
        var loop = CurveProcessing.DensifyLoop(Square);
        var pass = CurveProcessing.CreateOffsetCurveWithInfo(loop, d, closed: true).Points;
        Assume.That(TightestRadius(pass, true), Is.LessThan(3), "sharp before");

        var limited = CurveProcessing.LimitTurnRadius(pass, 6.0, closed: true);

        Assert.Multiple(() =>
        {
            Assert.That(TightestRadius(limited, true), Is.GreaterThan(5.5), "no bend tighter than the radius");
            // The sides stay where they were: a point mid-side is still d inside.
            Assert.That(limited.Where(p => Math.Abs(p.Northing) < 10 && p.Easting < 0).Min(p => p.Easting + 50), Is.EqualTo(d).Within(0.05));
            // The corner is cut on its inside, not pushed out.
            Assert.That(limited.All(p => Math.Abs(p.Easting) <= 50 - d + 0.05 && Math.Abs(p.Northing) <= 50 - d + 0.05));
            Assert.That(limited[0].Easting, Is.EqualTo(limited[^1].Easting).Within(1e-9), "still closed");
        });
    }

    [Test]
    public void Gentle_curve_is_left_as_it_is()
    {
        var arc = Enumerable.Range(0, 60).Select(i => i * Math.PI / 180).Select(a => new Vec3(100 * Math.Sin(a), 100 * Math.Cos(a), 0)).ToList();
        var same = CurveProcessing.LimitTurnRadius(arc, 6.0, closed: false);
        Assert.That(same, Is.SameAs(arc));
    }

    [Test]
    public void Open_line_keeps_its_ends()
    {
        var bend = CurveProcessing.Densify(new List<Vec3> { new(0, -50, 0), new(0, 0, 0), new(50, 0, 0) }, 1.0);
        var limited = CurveProcessing.LimitTurnRadius(bend, 8.0, closed: false);
        Assert.Multiple(() =>
        {
            Assert.That(Dist(limited[0], bend[0]), Is.LessThan(1e-9));
            Assert.That(Dist(limited[^1], bend[^1]), Is.LessThan(1e-9));
            Assert.That(TightestRadius(limited, false), Is.GreaterThan(7.5));
        });
    }
}
