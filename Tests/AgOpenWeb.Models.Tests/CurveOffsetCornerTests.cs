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

    // The point grid finds the same points as scanning them all, on curves long enough to use it.
    [Test]
    public void Offset_with_the_point_grid_matches_the_plain_scan()
    {
        var rng = new Random(450);
        for (int run = 0; run < 300; run++)
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
            CurveProcessing.CalculateHeadings(pts);
            double d = (rng.NextDouble() < 0.5 ? -1 : 1) * (0.2 + rng.NextDouble() * 60);

            var (actual, percent) = CurveProcessing.CreateOffsetCurveWithInfo(pts, d);
            if (PlainScan(pts, d) is not var (expected, expectedPercent)) continue; // nothing left: the two-point fallback

            Assert.That(percent, Is.EqualTo(expectedPercent), $"run {run}");
            Assert.That(actual.Count, Is.EqualTo(expected.Count), $"run {run}");
            for (int i = 0; i < actual.Count; i++)
            {
                Assert.That(actual[i].Easting, Is.EqualTo(expected[i].Easting), $"run {run} point {i}");
                Assert.That(actual[i].Northing, Is.EqualTo(expected[i].Northing), $"run {run} point {i}");
            }
        }
    }

    // CreateOffsetCurveWithInfo before the point grid: every offset point tested against every point.
    private static (List<Vec3>, double)? PlainScan(IReadOnlyList<Vec3> pts, double d)
    {
        double distSqAway = d * d - 0.01;
        int skipped = 0;
        var result = new List<Vec3>();
        foreach (var pt in pts)
        {
            double a = pt.Heading + Math.PI / 2;
            double e = pt.Easting + Math.Sin(a) * d, n = pt.Northing + Math.Cos(a) * d;
            if (pts.Any(t => (e - t.Easting) * (e - t.Easting) + (n - t.Northing) * (n - t.Northing) < distSqAway)) { skipped++; continue; }
            if (result.Count > 0)
            {
                var last = result[^1];
                if ((e - last.Easting) * (e - last.Easting) + (n - last.Northing) * (n - last.Northing) < 1.0) continue;
            }
            result.Add(new Vec3(e, n, pt.Heading));
        }
        if (result.Count < 2) return null;
        if (skipped > 0 && result.Count > 2) result = CurveProcessing.ChaikinsSmooth(result, 1);
        CurveProcessing.CalculateHeadings(result);
        return (result, skipped * 100.0 / pts.Count);
    }
}
