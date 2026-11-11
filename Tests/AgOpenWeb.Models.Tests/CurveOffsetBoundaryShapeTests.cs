// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A boundary curve's inward passes keep their distance from the fence and stay in the field,
/// whatever its shape. On Olival (Path job scenarios P1) the 12 m pass ran 5 m out of the field at
/// the tip of a thin spike in the boundary (LAP-LINE-LEAVES-FIELD: the tip's own offset point is
/// exactly the offset from the tip and passed the collision test), and the passes 20 to 36 m in
/// came 2 m nearer the fence than their distance where a short zigzag bumps into the field
/// (OFF-ITS-PASS: no arc round the bump, a chord across the gap). The spike is Olival's own: it
/// sits in a notch the boundary winds round, which a small made-up shape doesn't reproduce.
/// </summary>
[TestFixture]
public class CurveOffsetBoundaryShapeTests
{
    // Clockwise rings: + offsets are inward.

    /// <summary>A 100 m square whose west side zigzags 6 m into the field over 14 m.</summary>
    private static readonly Vec3[] SquareWithZigzag =
    {
        new(-50, -50, 0), new(-50, -10, 0), new(-44, -3, 0), new(-50, 4, 0), new(-50, 50, 0), new(50, 50, 0), new(50, -50, 0),
    };

    [TestCase(20)]
    [TestCase(28)]
    [TestCase(36)]
    public void Round_a_zigzag_into_the_field_the_inward_pass_keeps_its_distance(double d)
    {
        var (_, nearer) = Measure(SquareWithZigzag, d);
        Assert.That(nearer, Is.LessThan(0.15), $"the {d} m pass comes {nearer:F2} m nearer the fence than {d} m");
    }

    [Test]
    public void Olival_every_inward_lap_stays_in_the_field_at_its_distance()
    {
        string root = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(root, "AgOpenWeb.sln"))) root = Path.GetDirectoryName(root)!;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Tests", "AgOpenWeb.FieldJobScenarios.Tests", "TestData", "fields.json")));
        var field = doc.RootElement.GetProperty("fields").EnumerateArray().First(f => f.GetProperty("name").GetString() == "Olival");
        var outer = field.GetProperty("outer").EnumerateArray().Select(p => (Lat: p[0].GetDouble(), Lon: p[1].GetDouble())).ToList();
        double lat0 = outer.Average(p => p.Lat), lon0 = outer.Average(p => p.Lon);
        double mLat = 111320, mLon = 111320 * Math.Cos(lat0 * Math.PI / 180);
        var ring = outer.Select(p => new Vec3((p.Lon - lon0) * mLon, (p.Lat - lat0) * mLat, 0)).ToArray();

        Assert.Multiple(() =>
        {
            for (double d = 4; d <= 44; d += 8)
            {
                var (outside, nearer) = Measure(ring, d);
                Assert.That(outside, Is.LessThan(0.05), $"the {d} m lap runs {outside:F2} m out of the field");
                Assert.That(nearer, Is.LessThan(0.15), $"the {d} m lap comes {nearer:F2} m nearer the fence than {d} m");
            }
        });
    }

    /// <summary>The <paramref name="d"/> m inward pass of the ring as a boundary curve (as the app
    /// builds it), sampled every 0.25 m along its segments: how far out of the field it gets at
    /// worst, and how much nearer the fence than <paramref name="d"/>.</summary>
    private static (double Outside, double Nearer) Measure(IReadOnlyList<Vec3> ring, double d)
    {
        var curve = CurveProcessing.DensifyLoop(ring);
        var pass = CurveProcessing.CreateOffsetCurveWithInfo(curve, d, closed: true).Points;
        Assert.That(pass.Count, Is.GreaterThan(10), $"the {d} m pass exists");
        double outside = 0, nearer = 0;
        for (int j = 0; j + 1 < pass.Count; j++)
        {
            var a = pass[j]; var b = pass[j + 1];
            double len = Math.Sqrt(Math.Pow(b.Easting - a.Easting, 2) + Math.Pow(b.Northing - a.Northing, 2));
            int n = Math.Max(1, (int)(len / 0.25));
            for (int k = 0; k < n; k++)
            {
                double e = a.Easting + (b.Easting - a.Easting) * k / n, nn = a.Northing + (b.Northing - a.Northing) * k / n;
                double fence = Enumerable.Range(0, ring.Count).Min(i => SegDist(e, nn, ring[i], ring[(i + 1) % ring.Count]));
                if (!GeometryMath.IsPointInPolygon(ring, e, nn)) outside = Math.Max(outside, fence);
                else nearer = Math.Max(nearer, d - fence);
            }
        }
        return (outside, nearer);
    }

    private static double SegDist(double px, double py, Vec3 a, Vec3 b)
    {
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - a.Easting) * dx + (py - a.Northing) * dy) / l2, 0, 1);
        double ex = a.Easting + t * dx - px, ey = a.Northing + t * dy - py;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
