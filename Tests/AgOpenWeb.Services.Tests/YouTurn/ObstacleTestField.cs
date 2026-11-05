// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Geometry;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// "Quinta Torta": a test field with the obstacles a real farm has, for the closed-loop U-turn
/// scenarios. An irregular outer boundary about 350 × 480 m with a 20 m headland; ditches
/// square to the passes and at 15°, 45° and 75° to them; a wall 80 m long and a metre wide; a
/// round pond and a U-shaped one; a ditch from the fence into the field; a ditch from the
/// headland into the field; thirty lone trees as 6 m circles; poles and three clumps of trees drawn by hand
/// as trapezoids. The big ones are U-turn obstacles (one of them Soft, the rest Hard), the lone trees Soft
/// U-turn obstacles; the poles and tree clumps are driven through.
/// Field metres, origin at the centre.
/// </summary>
public static class ObstacleTestField
{
    public const double HeadlandWidth = 20;

    /// <summary>One obstacle of the field: its polygon, its name for the reports, and what it is.</summary>
    public sealed record Obstacle(string Name, BoundaryPolygon Polygon, double CentreE, double CentreN)
    {
        public bool IsUTurn => Polygon.AtObstacle == ObstacleAction.Turn;
        public bool IsHard => Polygon.IsHard;
    }

    public sealed class Built
    {
        public required Boundary Boundary { get; init; }
        public required List<Vec3> HeadlandLine { get; init; }
        public required IReadOnlyList<Obstacle> Obstacles { get; init; }
        public IEnumerable<Obstacle> UTurnObstacles => Obstacles.Where(o => o.IsUTurn);
        public BoundaryPolygon Outer => Boundary.OuterBoundary!;
    }

    public static readonly (double E, double N)[] OuterRing =
    {
        (-150, -230), (-60, -250), (40, -235), (120, -240), (165, -160), (175, -40), (160, 70),
        (170, 170), (110, 235), (10, 250), (-90, 230), (-160, 170), (-180, 40), (-155, -90), (-170, -170),
    };

    public static Built Build()
    {
        var outer = Polygon(OuterRing);
        outer.IsHard = true;
        var boundary = new Boundary { OuterBoundary = outer };
        var obstacles = new List<Obstacle>();

        void Add(string name, BoundaryPolygon p, ObstacleAction at, bool hard, double ce, double cn)
        {
            p.AtObstacle = at;
            p.IsHard = hard;
            boundary.InnerBoundaries.Add(p);
            obstacles.Add(new Obstacle(name, p, ce, cn));
        }

        // Ditches: square to north-south passes, and at angles to them.
        Add("Ditch square", Box(-55, 95, 20, 20, 0), ObstacleAction.Turn, true, -55, 95);
        Add("Ditch 15°", Box(45, 130, 60, 12, 15), ObstacleAction.Turn, true, 45, 130);
        Add("Ditch 45°", Box(-70, -40, 50, 10, 45), ObstacleAction.Turn, true, -70, -40);
        Add("Ditch 75° (Soft)", Box(95, -70, 50, 10, 75), ObstacleAction.Turn, false, 95, -70);
        // A wall: long and thin.
        Add("Wall", Box(-20, -150, 80, 1.2, 10), ObstacleAction.Turn, true, -20, -150);
        // Ponds.
        Add("Round pond", Circle(65, 30, 12), ObstacleAction.Turn, true, 65, 30);
        Add("U pond", Polygon(new[] { (-15.0, -12.0), (15, -12), (15, 12), (5, 12), (5, -2), (-5, -2), (-5, 12), (-15, 12) }
            .Select(p => (p.Item1 - 25, p.Item2 + 175)).ToArray()), ObstacleAction.Turn, true, -25, 175);
        // A ditch from the east fence 43 m into the field, and one from the south headland 43 m into the field.
        Add("Ditch from the fence", Box(146.5, -100, 43, 12, 90), ObstacleAction.Turn, true, 146.5, -100);
        Add("Ditch from the headland", Box(-15, -205, 50, 10, 0), ObstacleAction.Turn, true, -15, -205);

        // Thirty lone trees Renato placed by hand in the app as 6 m circles, singly and in clumps of
        // two to four (as close as 8 m apart): the small U-turn obstacles, Soft like his. A U-turn at a
        // tree smaller than the turn itself, with the next tree in the way of the turn.
        int circle = 0;
        foreach (var (e, n) in new[]
        {
            (-57.8, 65.4), (-81.1, -8.9), (-149.8, 31.6), (-155.9, 23.0), (-160.8, 37.7), (40.4, -29.7), (1.8, 14.4),
            (-10.5, 22.4), (-18.5, -2.1), (52.7, -70.2), (20.8, -85.0), (4.8, -77.0), (-12.3, -94.8), (41.6, -145.7),
            (64.3, -172.7), (35.5, -170.3), (11.0, -187.4), (77.4, 74.5), (102.7, 32.6), (19.9, 128.4), (25.1, 143.1),
            (36.4, 168.7), (16.0, 173.3), (-31.8, 211.3), (-15.9, 199.6), (-79.0, 113.0), (-89.2, 105.4), (-87.4, 117.0),
            (101.0, -9.9), (121.5, -43.4),
        })
            Add($"Tree circle {++circle}", Circle(e, n, 3), ObstacleAction.Turn, false, e, n);

        // Poles.
        int pole = 0;
        foreach (var (e, n) in new[] { (10.0, -60.0), (-100, 120), (30, 190), (120, 100), (-120, -150), (60, -170) })
            Add($"Pole {++pole}", Box(e, n, 0.5, 0.5, 0), ObstacleAction.DriveThrough, false, e, n);

        // Clumps of trees, drawn by hand as trapezoids.
        int tree = 0;
        foreach (var (ce, cn, count) in new[] { (-110.0, 20.0, 4), (20.0, 70.0, 3), (90.0, 180.0, 5) })
            for (int k = 0; k < count; k++)
            {
                double a = k * 2.4;
                double e = ce + 8 * Math.Cos(a), n = cn + 8 * Math.Sin(a);
                Add($"Tree {++tree}", Trapezoid(e, n, 5, k * 37), ObstacleAction.DriveThrough, false, e, n);
            }

        var headland = new PolygonOffsetService()
            .CreateInwardOffset(outer.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList(), HeadlandWidth)
            ?? throw new InvalidOperationException("no headland line");
        boundary.HeadlandPolygon = Polygon(headland.Select(p => (p.Easting, p.Northing)).ToArray());
        return new Built
        {
            Boundary = boundary,
            HeadlandLine = headland.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList(),
            Obstacles = obstacles,
        };
    }

    /// <summary>The tracks the scenarios drive: AB lines through the centre at a few headings, and a curve.</summary>
    public static Models.Track.Track Track(string kind) => kind switch
    {
        "AB 0°" => AbLine("AB 0°", 0),
        "AB 25°" => AbLine("AB 25°", 25),
        "AB 70°" => AbLine("AB 70°", 70),
        "Curve" => Models.Track.Track.FromCurve("Curve", Models.Guidance.CurveProcessing.CalculateHeadings(Enumerable.Range(0, 101)
            .Select(i => -260 + i * 5.2)
            .Select(n => new Vec3(18 * Math.Sin(n / 70.0), n, 0)).ToList())),
        _ => throw new ArgumentException(kind),
    };

    public static readonly string[] TrackKinds = { "AB 0°", "AB 25°", "AB 70°", "Curve" };

    private static Models.Track.Track AbLine(string name, double headingDeg)
    {
        double h = headingDeg * Math.PI / 180;
        return Models.Track.Track.FromABLine(name, new Vec3(-300 * Math.Sin(h), -300 * Math.Cos(h), 0),
            new Vec3(300 * Math.Sin(h), 300 * Math.Cos(h), 0));
    }

    public static BoundaryPolygon Polygon((double E, double N)[] ring)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in ring) p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    /// <summary>A rectangle <paramref name="length"/> along its own axis and <paramref name="width"/> across, the axis <paramref name="angleDeg"/> clockwise from north.</summary>
    public static BoundaryPolygon Box(double ce, double cn, double length, double width, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180, sa = Math.Sin(a), ca = Math.Cos(a);
        var ring = new List<(double, double)>();
        foreach (var (along, across) in new[] { (-length / 2, -width / 2), (-length / 2, width / 2), (length / 2, width / 2), (length / 2, -width / 2) })
            ring.Add((ce + sa * along + ca * across, cn + ca * along - sa * across));
        return Polygon(ring.ToArray());
    }

    public static BoundaryPolygon Circle(double ce, double cn, double radius, int sides = 24) =>
        Polygon(Enumerable.Range(0, sides).Select(i => 2 * Math.PI * i / sides)
            .Select(a => (ce + radius * Math.Cos(a), cn + radius * Math.Sin(a))).ToArray());

    private static BoundaryPolygon Trapezoid(double ce, double cn, double size, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180, sa = Math.Sin(a), ca = Math.Cos(a), s = size / 5;
        var ring = new[] { (-2.5 * s, -2.0 * s), (2.5 * s, -2.0 * s), (1.5 * s, 2.5 * s), (-1.8 * s, 2.5 * s) }
            .Select(p => (ce + ca * p.Item1 - sa * p.Item2, cn + sa * p.Item1 + ca * p.Item2)).ToArray();
        return Polygon(ring);
    }

    /// <summary>Signed distance from a point to a polygon's outline: negative inside.</summary>
    public static double SignedDistance(BoundaryPolygon polygon, double e, double n)
    {
        var pts = polygon.Points;
        double best = double.MaxValue;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            double ax = pts[j].Easting, ay = pts[j].Northing, bx = pts[i].Easting, by = pts[i].Northing;
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((e - ax) * dx + (n - ay) * dy) / len2, 0, 1) : 0;
            double px = ax + t * dx - e, py = ay + t * dy - n;
            best = Math.Min(best, Math.Sqrt(px * px + py * py));
        }
        return polygon.IsPointInside(e, n) ? -best : best;
    }
}
