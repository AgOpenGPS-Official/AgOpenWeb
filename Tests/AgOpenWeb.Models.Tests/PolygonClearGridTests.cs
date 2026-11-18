// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// <see cref="PolygonClearGrid"/> says "everything within this reach of the point is inside"
/// only where it is: never in a notch, never nearer the outline than the reach. And it says so
/// for the whole inside of a field but a strip along the fence, where the full test runs.
/// </summary>
[TestFixture]
public class PolygonClearGridTests
{
    private static List<Vec2> Points(params (double E, double N)[] corners)
    {
        var p = new List<Vec2>();
        foreach (var (e, n) in corners) p.Add(new Vec2(e, n));
        return p;
    }

    private static IEnumerable<TestCaseData> Shapes()
    {
        yield return new TestCaseData(Points((0, 0), (400, 0), (400, 150), (150, 150), (150, 250), (400, 250), (400, 400), (0, 400)))
            .SetArgDisplayNames("a notch from the east");
        yield return new TestCaseData(Points((0, 0), (400, 0), (400, 200), (200, 200), (200, 400), (0, 400)))
            .SetArgDisplayNames("an L");
        yield return new TestCaseData(Points((-130, 20), (40, -210), (310, -95), (190, 60), (335, 240), (60, 330), (-20, 150), (-240, 260)))
            .SetArgDisplayNames("slanted edges and a spike");

        // A boundary recorded by driving: a point every 2 m round a wavy oval.
        var oval = new List<Vec2>();
        for (int i = 0; i < 700; i++)
        {
            double a = 2 * Math.PI * i / 700, r = 1 + 0.25 * Math.Sin(3 * a);
            oval.Add(new Vec2(1000 + 260 * r * Math.Cos(a), -500 + 170 * r * Math.Sin(a)));
        }
        yield return new TestCaseData(oval).SetArgDisplayNames("a recorded oval");
    }

    private static double DistanceToOutline(List<Vec2> poly, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            double ax = poly[j].Easting, ay = poly[j].Northing, bx = poly[i].Easting - ax, by = poly[i].Northing - ay;
            double t = Math.Clamp(((e - ax) * bx + (n - ay) * by) / (bx * bx + by * by), 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(e - ax - t * bx, 2) + Math.Pow(n - ay - t * by, 2)));
        }
        return best;
    }

    [TestCaseSource(nameof(Shapes))]
    public void Clear_inside_only_where_the_outline_is_further_than_the_reach(List<Vec2> poly)
    {
        var slabs = PolygonSlabIndex.Build(poly)!;
        var grid = PolygonClearGrid.Build(poly, slabs)!;
        var rng = new Random(20261118);
        int clear = 0, deepInside = 0, deepInsideClear = 0;
        for (int k = 0; k < 40000; k++)
        {
            double e = slabs.MinEasting - 30 + rng.NextDouble() * (slabs.MaxEasting - slabs.MinEasting + 60);
            double n = slabs.MinNorthing - 30 + rng.NextDouble() * (slabs.MaxNorthing - slabs.MinNorthing + 60);
            double reach = k % 4 == 0 ? 0.5 + rng.NextDouble() * 40 : 0.5 + rng.NextDouble() * 6;
            bool inside = GeometryMath.IsPointInPolygon(poly, new Vec2(e, n));
            double toOutline = DistanceToOutline(poly, e, n);

            if (grid.IsClearInside(e, n, reach))
            {
                clear++;
                if (!inside || toOutline < reach)
                    Assert.Fail($"({e:0.0},{n:0.0}) reach {reach:0.0}: answered clear inside; inside {inside}, {toOutline:0.0} m from the outline");
            }
            // The grid counts whole 10 m cells the chessboard way, so across the cells' diagonal
            // it needs 1.42 times the reach plus two cells.
            if (inside && toOutline >= 1.42 * (reach + 20))
            {
                deepInside++;
                if (grid.IsClearInside(e, n, reach)) deepInsideClear++;
            }
        }
        Assert.Multiple(() =>
        {
            Assert.That(clear, Is.GreaterThan(1000), "the quick answer is given at all");
            Assert.That(deepInsideClear, Is.EqualTo(deepInside), "given everywhere far enough in: 1.42 x (the reach + 20 m) from the outline");
        });
    }

    [Test]
    public void No_grid_for_a_polygon_too_small_or_too_large()
    {
        var two = Points((0, 0), (10, 0));
        var huge = Points((0, 0), (50_000, 0), (50_000, 50_000), (0, 50_000));
        Assert.Multiple(() =>
        {
            Assert.That(PolygonClearGrid.Build(two, PolygonSlabIndex.Build(two)), Is.Null);
            Assert.That(PolygonClearGrid.Build(huge, PolygonSlabIndex.Build(huge)), Is.Null);
        });
    }

    /// <summary>The section test of a field takes the quick answer away from the fence: no
    /// polygon work at all (it runs for every section on every control tick).</summary>
    [Test]
    public void A_section_away_from_the_fence_costs_no_polygon_test()
    {
        var field = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (0.0, 0.0), (300.0, 0.0), (300.0, 500.0), (0.0, 500.0) })
            field.Points.Add(new BoundaryPoint(e, n, 0));
        field.UpdateBounds();

        var rng = new Random(7);
        long before = WorkCounters.PolygonTests;
        for (int k = 0; k < 5000; k++)
        {
            var centre = new Vec2(35 + rng.NextDouble() * 230, 35 + rng.NextDouble() * 430);
            Assert.That(field.GetSegmentBoundaryStatus(centre, rng.NextDouble() * 6.28, 3.0).IsFullyInside, Is.True);
        }
        Assert.That(WorkCounters.PolygonTests - before, Is.Zero);
    }
}
