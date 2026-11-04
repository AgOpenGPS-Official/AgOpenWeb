// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace AgOpenWeb.Models;

/// <summary>
/// A round obstacle, such as a tree: its centre in field metres and its diameter. The inner
/// boundary's polygon is made from it (<see cref="ToPoints"/>): a regular polygon whose sides
/// touch the circle, so all of the circle is inside the polygon. It is kept beside the polygon
/// so the obstacle can be edited as a circle; a polygon changed some other way is no longer
/// its circle (<see cref="Matches"/>).
/// </summary>
public readonly record struct ObstacleCircle(double CenterEasting, double CenterNorthing, double Diameter)
{
    /// <summary>The polygon's corners: its sides stay within 1 % of the radius of the circle.</summary>
    public const int Segments = 24;

    public const double MinDiameter = 0.5;
    public const double MaxDiameter = 100.0;

    /// <summary>
    /// How much bigger, by area, a circle made for every "Go around" obstacle at once may be than
    /// the drawn shape: a tree drawn by hand is 1.6 to 2.5 times smaller than its circle, a ditch
    /// or a row of stones far more, and keeps its shape.
    /// </summary>
    public const double MaxAreaRatio = 3.0;

    public double Radius => Diameter / 2.0;

    /// <summary>The circle's area, square metres.</summary>
    public double Area => Math.PI * Radius * Radius;

    /// <summary>A corner's distance from the centre, so that the sides touch the circle.</summary>
    public double CornerRadius => Radius / Math.Cos(Math.PI / Segments);

    /// <summary>A diameter an obstacle circle may have (false for NaN).</summary>
    public static bool IsValidDiameter(double diameter) => diameter >= MinDiameter && diameter <= MaxDiameter;

    /// <summary>The obstacle's polygon, counter-clockwise from east.</summary>
    public List<BoundaryPoint> ToPoints()
    {
        var points = new List<BoundaryPoint>(Segments);
        double r = CornerRadius;
        for (int k = 0; k < Segments; k++)
        {
            double a = 2.0 * Math.PI * k / Segments;
            points.Add(new BoundaryPoint(CenterEasting + r * Math.Cos(a), CenterNorthing + r * Math.Sin(a), 0));
        }
        return points;
    }

    /// <summary>
    /// True when <paramref name="points"/> are still this circle's polygon: as many corners,
    /// each at the corner distance within <paramref name="tolerance"/> metres.
    /// </summary>
    public bool Matches(IReadOnlyList<BoundaryPoint> points, double tolerance = 0.05)
    {
        if (points.Count != Segments) return false;
        double r = CornerRadius;
        foreach (var p in points)
        {
            double de = p.Easting - CenterEasting, dn = p.Northing - CenterNorthing;
            if (Math.Abs(Math.Sqrt(de * de + dn * dn) - r) > tolerance) return false;
        }
        return true;
    }

    /// <summary>
    /// The smallest circle that holds every one of <paramref name="points"/> (an obstacle drawn
    /// by hand), its diameter rounded up to 10 cm. Incremental, as Welzl's algorithm: an
    /// obstacle has a few dozen points.
    /// </summary>
    public static ObstacleCircle Covering(IReadOnlyList<BoundaryPoint> points)
    {
        if (points.Count == 0) throw new ArgumentException("No points", nameof(points));

        var c = (X: points[0].Easting, Y: points[0].Northing, R: 0.0);
        for (int i = 1; i < points.Count; i++)
        {
            var pi = points[i];
            if (Holds(c, pi)) continue;
            c = (pi.Easting, pi.Northing, 0.0);
            for (int j = 0; j < i; j++)
            {
                var pj = points[j];
                if (Holds(c, pj)) continue;
                c = OnDiameter(pi, pj);
                for (int k = 0; k < j; k++)
                {
                    var pk = points[k];
                    if (!Holds(c, pk)) c = Through(pi, pj, pk);
                }
            }
        }

        double diameter = Math.Ceiling(2.0 * c.R * 10.0 - 1e-6) / 10.0;
        return new ObstacleCircle(c.X, c.Y, Math.Max(diameter, MinDiameter));
    }

    private static bool Holds((double X, double Y, double R) c, BoundaryPoint p)
    {
        double de = p.Easting - c.X, dn = p.Northing - c.Y;
        return Math.Sqrt(de * de + dn * dn) <= c.R + 1e-7;
    }

    private static (double X, double Y, double R) OnDiameter(BoundaryPoint a, BoundaryPoint b)
    {
        double x = (a.Easting + b.Easting) / 2.0, y = (a.Northing + b.Northing) / 2.0;
        double de = a.Easting - x, dn = a.Northing - y;
        return (x, y, Math.Sqrt(de * de + dn * dn));
    }

    // The circle through three points; for three in a line, the one on the farthest two.
    private static (double X, double Y, double R) Through(BoundaryPoint a, BoundaryPoint b, BoundaryPoint c)
    {
        double bx = b.Easting - a.Easting, by = b.Northing - a.Northing;
        double cx = c.Easting - a.Easting, cy = c.Northing - a.Northing;
        double d = 2.0 * (bx * cy - by * cx);
        if (Math.Abs(d) < 1e-12)
        {
            var ab = OnDiameter(a, b); var ac = OnDiameter(a, c); var bc = OnDiameter(b, c);
            return ab.R >= ac.R && ab.R >= bc.R ? ab : ac.R >= bc.R ? ac : bc;
        }
        double b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
        double ux = (cy * b2 - by * c2) / d, uy = (bx * c2 - cx * b2) / d;
        return (a.Easting + ux, a.Northing + uy, Math.Sqrt(ux * ux + uy * uy));
    }
}
