// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A round obstacle (<see cref="ObstacleCircle"/>): its polygon holds the whole circle, a polygon
/// is recognised as its circle's only while unchanged, and a drawn shape's covering circle is
/// the smallest that holds it.
/// </summary>
[TestFixture]
public class ObstacleCircleTests
{
    private static List<BoundaryPoint> Pts(params (double e, double n)[] p) =>
        p.Select(q => new BoundaryPoint(q.e, q.n, 0)).ToList();

    [Test]
    public void ThePolygon_HoldsTheWholeCircle_AndHugsIt()
    {
        var c = new ObstacleCircle(10, -20, 6);
        var poly = new BoundaryPolygon();
        poly.SetCircle(c);

        Assert.That(poly.Points, Has.Count.EqualTo(ObstacleCircle.Segments));
        Assert.That(poly.Circle, Is.EqualTo(c));
        for (int k = 0; k < 360; k++)
        {
            double a = k * Math.PI / 180;
            Assert.That(poly.IsPointInside(10 + 2.999 * Math.Cos(a), -20 + 2.999 * Math.Sin(a)), Is.True, $"circle at {k} deg");
        }
        Assert.That(poly.IsPointInside(10 + 3.05, -20), Is.False, "the polygon is no more than 1 % outside the circle");
        Assert.That(poly.LongestSpan(), Is.EqualTo(6).Within(0.06));
    }

    [Test]
    public void APolygon_IsItsCircles_OnlyWhileUnchanged()
    {
        var c = new ObstacleCircle(0, 0, 8);
        var pts = c.ToPoints();
        Assert.That(c.Matches(pts), Is.True);
        Assert.That((c with { Diameter = 8.2 }).Matches(pts), Is.False, "another diameter");
        Assert.That((c with { CenterEasting = 0.2 }).Matches(pts), Is.False, "another centre");

        var moved = c.ToPoints();
        moved[5].Easting += 0.3;
        Assert.That(c.Matches(moved), Is.False, "a corner moved in another program");
        Assert.That(c.Matches(pts.Take(23).ToList()), Is.False, "a corner deleted");
    }

    [Test]
    public void TheCoveringCircle_OfASquare_GoesThroughItsCorners()
    {
        var c = ObstacleCircle.Covering(Pts((0, 0), (4, 0), (4, 4), (0, 4)));

        Assert.That(c.CenterEasting, Is.EqualTo(2).Within(1e-9));
        Assert.That(c.CenterNorthing, Is.EqualTo(2).Within(1e-9));
        Assert.That(c.Diameter, Is.EqualTo(5.7), "4 x sqrt 2 = 5.66, rounded up to 10 cm");
    }

    [Test]
    public void TheCoveringCircle_OfAnObtuseTriangle_IsOnItsLongestSide()
    {
        // The circle through the three corners would be far bigger.
        var c = ObstacleCircle.Covering(Pts((0, 0), (10, 0), (5, 1)));

        Assert.That(c.CenterEasting, Is.EqualTo(5).Within(1e-9));
        Assert.That(c.CenterNorthing, Is.EqualTo(0).Within(1e-9));
        Assert.That(c.Diameter, Is.EqualTo(10));
    }

    [Test]
    public void TheCoveringCircle_OfPointsInALine_IsOnTheFarthestTwo()
    {
        var c = ObstacleCircle.Covering(Pts((0, 0), (3, 3), (1, 1), (2, 2)));

        Assert.That(c.CenterEasting, Is.EqualTo(1.5).Within(1e-9));
        Assert.That(c.Diameter, Is.EqualTo(4.3), "3 x sqrt 2 = 4.24");
    }

    [Test]
    public void TheCoveringCircle_HoldsEveryPoint_AndIsTheSmallest()
    {
        var rnd = new Random(7);
        for (int run = 0; run < 200; run++)
        {
            int n = rnd.Next(3, 30);
            var pts = Enumerable.Range(0, n).Select(_ => new BoundaryPoint(rnd.NextDouble() * 12 - 6, rnd.NextDouble() * 8 - 4, 0)).ToList();

            var c = ObstacleCircle.Covering(pts);

            foreach (var p in pts)
                Assert.That(Math.Sqrt(Math.Pow(p.Easting - c.CenterEasting, 2) + Math.Pow(p.Northing - c.CenterNorthing, 2)),
                    Is.LessThanOrEqualTo(c.Radius + 1e-9), $"run {run}");
            // The smallest: the brute-force best over every pair and triple of points.
            Assert.That(c.Diameter, Is.LessThanOrEqualTo(Math.Ceiling(2 * BruteForceRadius(pts) * 10 - 1e-6) / 10 + 1e-9), $"run {run}");
        }
    }

    [Test]
    public void ADiameter_IsHalfAMetreToAHundred()
    {
        Assert.That(ObstacleCircle.IsValidDiameter(0.5), Is.True);
        Assert.That(ObstacleCircle.IsValidDiameter(100), Is.True);
        Assert.That(ObstacleCircle.IsValidDiameter(0.4), Is.False);
        Assert.That(ObstacleCircle.IsValidDiameter(101), Is.False);
        Assert.That(ObstacleCircle.IsValidDiameter(double.NaN), Is.False);
    }

    private static double BruteForceRadius(List<BoundaryPoint> pts)
    {
        double best = double.MaxValue;
        bool Holds(double x, double y, double r) =>
            pts.All(p => Math.Sqrt(Math.Pow(p.Easting - x, 2) + Math.Pow(p.Northing - y, 2)) <= r + 1e-9);
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                double x = (pts[i].Easting + pts[j].Easting) / 2, y = (pts[i].Northing + pts[j].Northing) / 2;
                double r = Math.Sqrt(Math.Pow(pts[i].Easting - x, 2) + Math.Pow(pts[i].Northing - y, 2));
                if (r < best && Holds(x, y, r)) best = r;
                for (int k = j + 1; k < pts.Count; k++)
                {
                    double ax = pts[i].Easting, ay = pts[i].Northing;
                    double bx = pts[j].Easting - ax, by = pts[j].Northing - ay, cx = pts[k].Easting - ax, cy = pts[k].Northing - ay;
                    double d = 2 * (bx * cy - by * cx);
                    if (Math.Abs(d) < 1e-12) continue;
                    double ux = (cy * (bx * bx + by * by) - by * (cx * cx + cy * cy)) / d;
                    double uy = (bx * (cx * cx + cy * cy) - cx * (bx * bx + by * by)) / d;
                    double rr = Math.Sqrt(ux * ux + uy * uy);
                    if (rr < best && Holds(ax + ux, ay + uy, rr)) best = rr;
                }
            }
        return best;
    }
}
