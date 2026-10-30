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
/// #450: the passes of a boundary curve. The fence's corners alone gave the collision test
/// nothing to see, so inward passes kept sharp corners skewed into the field; a closed loop's
/// passes stopped short at the seam; and a long curve's test scanned every point per point.
/// </summary>
[TestFixture]
public class CurveOffsetCornerTests
{
    private static readonly Vec3[] Square = { new(-50, -50, 0), new(-50, 50, 0), new(50, 50, 0), new(50, -50, 0) };

    // Distance from a point to the square's sides.
    private static double ToSides(Vec3 p) =>
        Math.Min(Math.Min(Math.Abs(p.Easting + 50), Math.Abs(p.Easting - 50)), Math.Min(Math.Abs(p.Northing + 50), Math.Abs(p.Northing - 50)));

    [Test]
    public void DensifyLoop_puts_a_point_every_1_1_m_on_a_small_field_and_closes_it()
    {
        var loop = CurveProcessing.DensifyLoop(Square);
        Assert.Multiple(() =>
        {
            Assert.That(loop[0].Easting, Is.EqualTo(loop[^1].Easting));
            Assert.That(loop[0].Northing, Is.EqualTo(loop[^1].Northing));
            for (int i = 0; i + 1 < loop.Count; i++)
                Assert.That(Math.Sqrt(Math.Pow(loop[i + 1].Easting - loop[i].Easting, 2) + Math.Pow(loop[i + 1].Northing - loop[i].Northing, 2)),
                    Is.LessThanOrEqualTo(1.1 + 1e-9));
            foreach (var corner in Square)
                Assert.That(loop.Any(p => p.Easting == corner.Easting && p.Northing == corner.Northing), "the fence's corners are kept");
        });
    }

    // Clockwise loop: right of travel is inward. Every inward pass stays its distance clear of the
    // fence (a skewed corner came to within 0 m of the next side) and is closed at the seam.
    [Test]
    public void Inward_passes_of_a_boundary_curve_round_their_corners_and_close([Values(3.0, 9.0, 21.0, 39.0)] double d)
    {
        var loop = CurveProcessing.DensifyLoop(Square);
        var (pass, _) = CurveProcessing.CreateOffsetCurveWithInfo(loop, d, closed: true);

        Assert.Multiple(() =>
        {
            Assert.That(pass.Min(ToSides), Is.GreaterThan(d - 0.6), "no point pinches toward a corner");
            Assert.That(pass.All(p => Math.Abs(p.Easting) < 50 && Math.Abs(p.Northing) < 50), "inside the fence");
            Assert.That(pass[0].Easting, Is.EqualTo(pass[^1].Easting).Within(1e-9), "closed at the seam");
            Assert.That(pass[0].Northing, Is.EqualTo(pass[^1].Northing).Within(1e-9), "closed at the seam");
        });
    }

    [Test]
    public void Inward_pass_of_the_bare_fence_skews_its_corners()
    {
        // What the Field Builder curve was: the four corners only. Kept to show the failure the
        // densified loop avoids.
        var bare = CurveProcessing.CalculateHeadings(new List<Vec3>(Square) { Square[0] });
        var pass = CurveProcessing.CreateOffsetCurve(bare, 9.0);
        Assert.That(pass.Min(ToSides), Is.LessThan(1.0));
    }

    // The largest change of heading from one segment of a line to the next (degrees).
    private static double SharpestTurn(IReadOnlyList<Vec3> pts)
    {
        double worst = 0;
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            double h1 = Math.Atan2(pts[i].Easting - pts[i - 1].Easting, pts[i].Northing - pts[i - 1].Northing);
            double h2 = Math.Atan2(pts[i + 1].Easting - pts[i].Easting, pts[i + 1].Northing - pts[i].Northing);
            worst = Math.Max(worst, Math.Abs(Math.IEEERemainder(h2 - h1, 2 * Math.PI)) * 180 / Math.PI);
        }
        return worst;
    }

    // A driven curve with a right-angle bend, offset to the outside of the bend: the offset jumped
    // from one leg's normal to the other's, leaving a chord with a 45° kink at each end that the
    // tractor overshot. It is now an arc about the bend, as a true parallel offset is.
    [Test]
    public void Outside_of_a_bend_is_an_arc_not_a_chord([Values(6.0, 20.0, 60.0)] double d)
    {
        var bend = CurveProcessing.Densify(new List<Vec3> { new(0, -100, 0), new(0, 0, 0), new(100, 0, 0) }, 1.0);
        var pass = CurveProcessing.CreateOffsetCurve(bend, -d); // left of travel: the outside of a right turn

        Assert.Multiple(() =>
        {
            Assert.That(SharpestTurn(pass), Is.LessThan(12), "no kink");
            var gaps = pass.Zip(pass.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.Easting - a.Easting, 2) + Math.Pow(b.Northing - a.Northing, 2)));
            Assert.That(gaps.Max(), Is.LessThan(3.5), "no chord across the corner (it was d·√2)");
            // Every point is the offset from the bend's line: no point closer, none further at the corner.
            Assert.That(pass.Min(p => Math.Min(Math.Abs(p.Easting) + (p.Northing > 0 ? 1e9 : 0), Math.Sqrt(p.Easting * p.Easting + p.Northing * p.Northing))),
                Is.GreaterThan(d - 0.01));
        });
    }

    // The inside of the same bend keeps its corner: the collision test removes the points there.
    [Test]
    public void Inside_of_a_bend_gets_no_arc()
    {
        var bend = CurveProcessing.Densify(new List<Vec3> { new(0, -100, 0), new(0, 0, 0), new(100, 0, 0) }, 1.0);
        var pass = CurveProcessing.CreateOffsetCurve(bend, 10);
        Assert.That(pass.All(p => p.Easting >= 9.5 && p.Northing <= -9.5), "nothing toward the corner");
    }

    // The point grid gives the plain scan's answer, for random curves and offsets.
    [Test]
    public void Point_grid_answers_like_the_plain_scan()
    {
        var rng = new Random(450);
        for (int run = 0; run < 200; run++)
        {
            int n = rng.Next(65, 400);
            var pts = new List<Vec3>(n);
            double e = 0, nn = 0, h = rng.NextDouble() * 2 * Math.PI;
            for (int i = 0; i < n; i++)
            {
                h += (rng.NextDouble() - 0.5) * 0.6;
                e += Math.Sin(h) * (0.5 + rng.NextDouble() * 3);
                nn += Math.Cos(h) * (0.5 + rng.NextDouble() * 3);
                pts.Add(new Vec3(e, nn, 0));
            }
            double d = 0.2 + rng.NextDouble() * 60;
            var grid = new CurveProcessing.PointGrid(pts, d);
            for (int q = 0; q < 200; q++)
            {
                var p = pts[rng.Next(n)];
                double qe = p.Easting + (rng.NextDouble() - 0.5) * 3 * d, qn = p.Northing + (rng.NextDouble() - 0.5) * 3 * d;
                Assert.That(grid.AnyCloser(pts, qe, qn, d * d - 0.01), Is.EqualTo(CurveProcessing.AnyCloser(pts, qe, qn, d * d - 0.01)), $"run {run}");
            }
        }
    }

    // A driven line's moving average keeps a straight line where it is (centred, unlike
    // AgOpenGPS's SmoothAB, whose window sits a point behind) and evens out a wobble.
    [Test]
    public void Moving_average_keeps_a_straight_line_and_evens_a_wobble()
    {
        var straight = Enumerable.Range(0, 30).Select(i => new Vec3(5, i * 1.0, 0)).ToList();
        var wobble = Enumerable.Range(0, 30).Select(i => new Vec3(5 + (i % 2 == 0 ? 0.2 : -0.2), i * 1.0, 0)).ToList();

        var s = CurveProcessing.SmoothMovingAverage(straight, 2);
        var w = CurveProcessing.SmoothMovingAverage(wobble, 2);

        Assert.Multiple(() =>
        {
            for (int i = 0; i < s.Count; i++)
            {
                Assert.That(s[i].Easting, Is.EqualTo(5).Within(1e-9));
                Assert.That(s[i].Northing, Is.EqualTo(i).Within(1e-9), "not shifted along the line");
            }
            Assert.That(w.Skip(2).Take(w.Count - 4).Max(p => Math.Abs(p.Easting - 5)), Is.LessThan(0.05));
        });
    }
}
