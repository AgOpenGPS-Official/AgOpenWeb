// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// One point-in-polygon rule (<see cref="GeometryMath.CrossesSouth"/>) for every test in the
/// app, and the strip index (<see cref="PolygonSlabIndex"/>, used by BoundaryPolygon, and
/// through <see cref="PolygonIndexCache"/> by section control, the GPS cycle and U-turn
/// planning) giving the same answers as walking every edge.
/// </summary>
[TestFixture]
public class PointInPolygonTests
{
    private static List<Vec2> Square(double e0, double n0, double size) =>
        new() { new(e0, n0), new(e0 + size, n0), new(e0 + size, n0 + size), new(e0, n0 + size) };

    [TestCase(5, 5, true, TestName = "Centre")]
    [TestCase(0, 5, false, TestName = "West edge is outside")]
    [TestCase(5, 0, false, TestName = "South edge is outside")]
    [TestCase(10, 5, true, TestName = "East edge is inside")]
    [TestCase(5, 10, true, TestName = "North edge is inside")]
    [TestCase(0, 0, false, TestName = "South-west corner is outside")]
    [TestCase(10, 10, true, TestName = "North-east corner is inside")]
    [TestCase(-0.001, 5, false, TestName = "Just west")]
    [TestCase(10.001, 5, false, TestName = "Just east")]
    public void TheEdgeRule(double e, double n, bool inside)
    {
        var square = Square(0, 0, 10);
        Assert.Multiple(() =>
        {
            Assert.That(GeometryMath.IsPointInPolygon(square, e, n), Is.EqualTo(inside), "full walk");
            Assert.That(PolygonSlabIndex.Build(square)!.Contains(e, n), Is.EqualTo(inside), "strip index");
        });
    }

    [Test]
    public void APointOnASharedEdge_BelongsToOnePolygon()
    {
        var west = Square(0, 0, 10);
        var east = Square(10, 0, 10);
        var north = Square(0, 10, 10);
        for (double n = 0.5; n < 10; n += 0.5)
            Assert.That(GeometryMath.IsPointInPolygon(west, 10, n) ^ GeometryMath.IsPointInPolygon(east, 10, n), Is.True, $"e=10 n={n}");
        for (double e = 0.5; e < 10; e += 0.5)
            Assert.That(GeometryMath.IsPointInPolygon(west, e, 10) ^ GeometryMath.IsPointInPolygon(north, e, 10), Is.True, $"e={e} n=10");
    }

    // Shapes the index must agree on: convex, concave, rotated, densified, with collinear runs.
    private static IEnumerable<(string Name, List<Vec2> Points)> Shapes()
    {
        yield return ("square", Square(0, 0, 20));
        yield return ("U-shape", new List<Vec2> { new(0, 0), new(30, 0), new(30, 30), new(20, 30), new(20, 10), new(10, 10), new(10, 30), new(0, 30) });
        var rot = new List<Vec2>();
        for (int k = 0; k < 7; k++)
        {
            double a = k * 2 * Math.PI / 7 + 0.3, r = k % 2 == 0 ? 40 : 18;
            rot.Add(new Vec2(r * Math.Cos(a), r * Math.Sin(a)));
        }
        yield return ("rotated star", rot);
        var ring = new List<Vec2>(); // a driven boundary: a point a metre, collinear runs
        for (double x = -60; x < 60; x++) ring.Add(new(x, -40));
        for (double y = -40; y < 40; y++) ring.Add(new(60, y));
        for (double x = 60; x > -60; x--) ring.Add(new(x, 40 + 6 * Math.Sin(x / 9)));
        for (double y = 40; y > -40; y--) ring.Add(new(-60, y));
        yield return ("densified ring", ring);
    }

    [Test]
    public void TheIndex_AgreesWithTheFullWalk_EverywhereIncludingOnEdges()
    {
        var rng = new Random(418);
        foreach (var (name, poly) in Shapes())
        {
            var index = PolygonSlabIndex.Build(poly)!;
            // Integer and half-metre points land on edges and vertices; random ones fill in.
            var points = new List<(double E, double N)>();
            for (double e = -70; e <= 70; e += 0.5)
                for (double n = -50; n <= 50; n += 0.5) points.Add((e, n));
            for (int k = 0; k < 20000; k++) points.Add((rng.NextDouble() * 140 - 70, rng.NextDouble() * 100 - 50));
            foreach (var v in poly) points.Add((v.Easting, v.Northing));

            int mismatches = points.Count(p => index.Contains(p.E, p.N) != GeometryMath.IsPointInPolygon(poly, p.E, p.N));
            Assert.That(mismatches, Is.Zero, name);
        }
    }

    [Test]
    public void BoundaryPolygon_AgreesWithTheFullWalk()
    {
        foreach (var (name, poly) in Shapes())
        {
            var bp = new BoundaryPolygon { Points = poly.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() };
            for (double e = -70; e <= 70; e += 0.5)
                for (double n = -50; n <= 50; n += 0.5)
                    if (bp.IsPointInside(e, n) != GeometryMath.IsPointInPolygon(poly, e, n))
                        Assert.Fail($"{name}: ({e}, {n})");
        }
    }

    [Test]
    public void TheIndex_VisitsAFewEdges_NotAll()
    {
        var ring = Shapes().Single(s => s.Name == "densified ring").Points; // 480 points
        var index = PolygonSlabIndex.Build(ring)!;
        long before = WorkCounters.PolygonEdges;
        index.Contains(3.3, 1.1);
        Assert.That(WorkCounters.PolygonEdges - before, Is.LessThan(20));
    }

    [Test]
    public void BoundaryPolygon_FollowsItsPoints()
    {
        var bp = new BoundaryPolygon { Points = Square(0, 0, 10).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() };
        Assert.That(bp.IsPointInside(5, 5), Is.True);

        // Replaced: rebuilt without being told.
        bp.Points = Square(100, 100, 10).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(bp.IsPointInside(5, 5), Is.False);
            Assert.That(bp.IsPointInside(105, 105), Is.True);
        });

        // A point added: rebuilt (the count changed).
        bp.Points.Insert(2, new BoundaryPoint(130, 105, 0));
        Assert.That(bp.IsPointInside(120, 105), Is.True);

        // Moved in place: UpdateBounds, as its comment says.
        foreach (var p in bp.Points) p.Easting -= 100;
        bp.UpdateBounds();
        Assert.That(bp.IsPointInside(5, 105), Is.True);

        // Too few points: outside, not an exception.
        bp.Points = new List<BoundaryPoint> { new(0, 0, 0), new(1, 1, 0) };
        Assert.That(bp.IsPointInside(0.5, 0.2), Is.False);
    }

    [Test]
    public void TheCache_AgreesWithTheFullWalk_EverywhereIncludingOnEdges()
    {
        foreach (var (name, poly) in Shapes())
        {
            var cache = new PolygonIndexCache();
            var points = new List<(double E, double N)>();
            for (double e = -70; e <= 70; e += 0.5)
                for (double n = -50; n <= 50; n += 0.5) points.Add((e, n));
            foreach (var v in poly) points.Add((v.Easting, v.Northing));

            int mismatches = points.Count(p => cache.Contains(poly, p.E, p.N) != GeometryMath.IsPointInPolygon(poly, p.E, p.N));
            Assert.That(mismatches, Is.Zero, name);
        }
    }

    [Test]
    public void TheCache_FollowsTheList()
    {
        var cache = new PolygonIndexCache();
        var first = Square(0, 0, 10);
        var index = cache.For(first);
        Assert.That(cache.Contains(first, 5, 5), Is.True);
        Assert.That(cache.For(first), Is.SameAs(index), "the same list: built once");

        // Replaced: rebuilt.
        var second = Square(100, 100, 10);
        Assert.Multiple(() =>
        {
            Assert.That(cache.Contains(second, 5, 5), Is.False);
            Assert.That(cache.Contains(second, 105, 105), Is.True);
        });

        // A point added to the same list: rebuilt (the count changed).
        second.Insert(2, new Vec2(130, 105));
        Assert.That(cache.Contains(second, 120, 105), Is.True);

        // Too few points: outside, no index.
        var line = new List<Vec2> { new(0, 0), new(1, 1) };
        Assert.Multiple(() =>
        {
            Assert.That(cache.Contains(line, 0.5, 0.2), Is.False);
            Assert.That(cache.For(line), Is.Null);
        });
    }
}
