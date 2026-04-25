// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class TangentLineBypassTests
{
    private static List<Vec2> Square(double x, double y, double size)
        => new()
        {
            new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size),
        };

    [Test]
    public void StraightConnector_ReturnsTwoPoints()
    {
        var path = TangentLineBypass.StraightConnector(new(0, 0), new(10, 0));
        Assert.That(path.Count, Is.EqualTo(2));
        Assert.That(path[0].Easting, Is.EqualTo(0));
        Assert.That(path[1].Easting, Is.EqualTo(10));
    }

    [Test]
    public void Bypass_DegenerateObstacle_ReturnsStraightLine()
    {
        // <3 hull vertices → can't form an obstacle.
        var path = TangentLineBypass.Bypass(new(0, 0), new(10, 0), new List<Vec2>());
        Assert.That(path.Count, Is.EqualTo(2));

        path = TangentLineBypass.Bypass(new(0, 0), new(10, 0),
            new List<Vec2> { new(5, 5), new(6, 5) });
        Assert.That(path.Count, Is.EqualTo(2));
    }

    [Test]
    public void SquareObstacle_BypassWalksOneSide()
    {
        // A and B on opposite sides (left/right) of a square obstacle.
        // Direct segment crosses obstacle; shortest bypass walks 2 hull
        // corners (one side of the square).
        var a = new Vec2(0, 5);
        var b = new Vec2(10, 5);
        var obstacle = Square(3, 3, 4);  // square at (3,3)-(7,7)

        var path = TangentLineBypass.Bypass(a, b, obstacle);

        Assert.That(path[0], Is.EqualTo(a));
        Assert.That(path[^1], Is.EqualTo(b));
        // Square has 4 vertices. Shortest bypass touches 2 corners on one side.
        // Path: A → corner1 → corner2 → B = 4 points.
        Assert.That(path.Count, Is.EqualTo(4));

        // Walked corners should be on the SAME side of the line A→B (y=5):
        // either both above (y > 5) or both below.
        bool allAbove = path[1].Northing > 5 && path[2].Northing > 5;
        bool allBelow = path[1].Northing < 5 && path[2].Northing < 5;
        Assert.That(allAbove || allBelow, Is.True,
            "Both walked corners should be on the same side of the AB line");
    }

    [Test]
    public void SquareObstacle_BypassIsLongerThanDirectButShortest()
    {
        // Bypass should be longer than direct but no longer than going around
        // the LONG way (the other side of the square).
        var a = new Vec2(0, 5);
        var b = new Vec2(10, 5);
        var obstacle = Square(3, 3, 4);

        var path = TangentLineBypass.Bypass(a, b, obstacle);
        double bypassLen = PolylineLength(path);
        double directLen = 10.0;
        // Going around either side of a 4×4 square between A=(0,5) and B=(10,5):
        //   A → (3,7) → (7,7) → B: sqrt(9+4) + 4 + sqrt(9+4) ≈ 3.606 + 4 + 3.606 = 11.21
        //   A → (3,3) → (7,3) → B: same by symmetry ≈ 11.21
        Assert.That(bypassLen, Is.GreaterThan(directLen));
        Assert.That(bypassLen, Is.LessThan(15.0));
        Assert.That(bypassLen, Is.EqualTo(11.211).Within(0.01));
    }

    [Test]
    public void PondObstacle_BypassIsSmoothAndShorterThanFarSide()
    {
        // Approximate pond as a 16-gon, radius 2 centered at (5, 5).
        // A and B on opposite sides (left/right). Bypass walks the upper
        // semicircle (or lower); both equal length by symmetry.
        var a = new Vec2(0, 5);
        var b = new Vec2(10, 5);
        var pond = ApproximateCircle(center: new(5, 5), radius: 2, segments: 16);

        var path = TangentLineBypass.Bypass(a, b, pond);

        Assert.That(path[0], Is.EqualTo(a));
        Assert.That(path[^1], Is.EqualTo(b));
        // Walks ~half the circle in vertices: 8/16 hull verts plus 2 endpoints.
        Assert.That(path.Count, Is.GreaterThanOrEqualTo(5));
        Assert.That(path.Count, Is.LessThanOrEqualTo(11));

        // Bypass length should be moderate: less than walking the OTHER 3/4 way
        // of the pond, and more than direct.
        double len = PolylineLength(path);
        Assert.That(len, Is.GreaterThan(10.0));
        Assert.That(len, Is.LessThan(14.0));
    }

    [Test]
    public void ConvexHull_OfRectangleIsTheRectangle()
    {
        var rect = Square(0, 0, 10);
        var hull = TangentLineBypass.ConvexHull(rect);
        Assert.That(hull.Count, Is.EqualTo(4));
    }

    [Test]
    public void ConvexHull_OfNonConvexShapeDropsInteriorPoints()
    {
        // Star-like shape with a couple of indented points; hull should be
        // the outer corners only.
        var pts = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 10), new(0, 10),
            new(5, 5),    // interior — must be dropped
            new(2, 5),    // interior — must be dropped
        };
        var hull = TangentLineBypass.ConvexHull(pts);
        Assert.That(hull.Count, Is.EqualTo(4),
            "interior points should not appear on the hull");
    }

    [Test]
    public void ConvexHull_FedToBypass_HandlesIrregularObstacle()
    {
        // Irregular L-shaped obstacle (concave). The reflex vertex (5,5) is
        // interior to the hull; the other 5 vertices stay on the hull
        // (including (5,7), which bulges above the line from (7,5) to (3,7)).
        var l = new List<Vec2>
        {
            new(3, 3), new(7, 3), new(7, 5), new(5, 5), new(5, 7), new(3, 7),
        };
        var hull = TangentLineBypass.ConvexHull(l);
        Assert.That(hull.Count, Is.EqualTo(5),
            "Convex hull drops the reflex vertex (5,5) but keeps the bulging tip (5,7)");
        Assert.That(hull, Does.Not.Contain(new Vec2(5, 5)));

        var path = TangentLineBypass.Bypass(new(0, 5), new(10, 5), hull);
        Assert.That(path.Count, Is.GreaterThanOrEqualTo(4));
    }

    private static List<Vec2> ApproximateCircle(Vec2 center, double radius, int segments)
    {
        var pts = new List<Vec2>(segments);
        for (int i = 0; i < segments; i++)
        {
            double t = 2.0 * Math.PI * i / segments;
            pts.Add(new Vec2(center.Easting + radius * Math.Cos(t),
                             center.Northing + radius * Math.Sin(t)));
        }
        return pts;
    }

    private static double PolylineLength(List<Vec2> path)
    {
        double sum = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double de = path[i].Easting - path[i - 1].Easting;
            double dn = path[i].Northing - path[i - 1].Northing;
            sum += Math.Sqrt(de * de + dn * dn);
        }
        return sum;
    }
}
