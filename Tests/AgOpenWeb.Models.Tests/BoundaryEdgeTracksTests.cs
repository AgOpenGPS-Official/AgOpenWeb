// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Track;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>All Edges: straight sides become AB lines, a curved side an AB curve (#365).</summary>
[TestFixture]
public class BoundaryEdgeTracksTests
{
    // Boundary points every metre or so along a side, like a densified boundary.
    private static void Side(List<BoundaryPoint> pts, double e0, double n0, double e1, double n1)
    {
        int steps = (int)Math.Ceiling(Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0)));
        for (int i = 0; i < steps; i++)
            pts.Add(new BoundaryPoint(e0 + (e1 - e0) * i / steps, n0 + (n1 - n0) * i / steps, 0));
    }

    private static List<BoundaryPoint> Rectangle()
    {
        var pts = new List<BoundaryPoint>();
        Side(pts, 0, 0, 0, 200);
        Side(pts, 0, 200, 100, 200);
        Side(pts, 100, 200, 100, 0);
        Side(pts, 100, 0, 0, 0);
        return pts;
    }

    // A 100 × 200 m field whose east side bows out 30 m in an arc.
    private static List<BoundaryPoint> CurvedEastSide()
    {
        var pts = new List<BoundaryPoint>();
        Side(pts, 0, 0, 0, 200);
        Side(pts, 0, 200, 100, 200);
        // Circle through (100,200), (130,100), (100,0): centre (-51.67,100), radius 181.67.
        double cx = -51.6667, r = 181.6667, a0 = Math.Asin(100 / r);
        for (int i = 0; i < 210; i++)
        {
            double a = a0 - 2 * a0 * i / 210;
            pts.Add(new BoundaryPoint(cx + r * Math.Cos(a), 100 + r * Math.Sin(a), 0));
        }
        Side(pts, 100, 0, 0, 0);
        return pts;
    }

    [Test]
    public void Rectangle_gives_four_AB_lines()
    {
        var tracks = BoundaryEdgeTracks.Build(Rectangle());
        Assert.That(tracks, Has.Count.EqualTo(4));
        Assert.That(tracks.All(t => t.Type == TrackType.ABLine && t.Points.Count == 2), Is.True);
    }

    // The rectangle as a driven boundary: corners rounded at a 6 m radius, a point every metre.
    private static List<BoundaryPoint> RoundedRectangle()
    {
        const double R = 6;
        var pts = new List<BoundaryPoint>();
        void Corner(double ce, double cn, double fromDeg)
        {
            for (int i = 0; i < 9; i++)
            {
                double a = (fromDeg - i * 10) * Math.PI / 180;
                pts.Add(new BoundaryPoint(ce + R * Math.Cos(a), cn + R * Math.Sin(a), 0));
            }
        }
        Side(pts, 0, R, 0, 200 - R); Corner(R, 200 - R, 180);
        Side(pts, R, 200, 100 - R, 200); Corner(100 - R, 200 - R, 90);
        Side(pts, 100, 200 - R, 100, R); Corner(100 - R, R, 0);
        Side(pts, 100 - R, 0, R, 0); Corner(R, R, 270);
        return pts;
    }

    [Test]
    public void Rounded_corners_still_split_the_sides()
    {
        var tracks = BoundaryEdgeTracks.Build(RoundedRectangle());
        Assert.That(tracks, Has.Count.EqualTo(4));
        Assert.That(tracks.All(t => t.Type == TrackType.ABLine), Is.True);
    }

    [Test]
    public void Each_line_lies_on_its_side()
    {
        // The west side is e = 0 from n = 0 to 200: the line runs along it, past both ends.
        foreach (var field in new[] { Rectangle(), RoundedRectangle() })
        {
            var west = BoundaryEdgeTracks.Build(field).Single(t => t.Points.All(p => Math.Abs(p.Easting) < 0.05));
            Assert.That(west.Points.Min(p => p.Northing), Is.LessThan(-40));
            Assert.That(west.Points.Max(p => p.Northing), Is.GreaterThan(240));
        }
    }

    [Test]
    public void Curved_side_becomes_a_curve_that_follows_it()
    {
        var tracks = BoundaryEdgeTracks.Build(CurvedEastSide());
        Assert.That(tracks, Has.Count.EqualTo(4));
        Assert.That(tracks.Count(t => t.Type == TrackType.ABLine), Is.EqualTo(3));

        var curve = tracks.Single(t => t.Type == TrackType.Curve);
        Assert.Multiple(() =>
        {
            Assert.That(curve.IsClosed, Is.False);
            // It follows the bow: its furthest point east is the arc's apex, not the chord.
            var inField = curve.Points.Where(p => p.Northing is > 0 and < 200).ToList();
            Assert.That(inField.Max(p => p.Easting), Is.EqualTo(130).Within(1.0));
            // And runs past both corners, like the AB lines.
            Assert.That(curve.Points.Min(p => p.Northing), Is.LessThan(-40));
            Assert.That(curve.Points.Max(p => p.Northing), Is.GreaterThan(240));
        });
    }

    [Test]
    public void Curve_headings_point_along_the_curve()
    {
        var curve = BoundaryEdgeTracks.Build(CurvedEastSide()).Single(t => t.Type == TrackType.Curve);
        var p = curve.Points;
        for (int i = 0; i < p.Count - 1; i++)
        {
            double h = Math.Atan2(p[i + 1].Easting - p[i].Easting, p[i + 1].Northing - p[i].Northing);
            double d = Math.Abs(Math.IEEERemainder(p[i].Heading - h, 2 * Math.PI));
            Assert.That(d, Is.LessThan(0.035), $"point {i}"); // 2°: the extensions follow a smoothed end direction
        }
    }

    [Test]
    public void Wobble_within_a_metre_stays_an_AB_line()
    {
        var pts = Rectangle();
        for (int i = 0; i < pts.Count; i++)
            if (pts[i].Easting == 0 && pts[i].Northing is > 0 and < 200)
                pts[i] = new BoundaryPoint(0.6 * Math.Sin(pts[i].Northing / 7), pts[i].Northing, 0);
        Assert.That(BoundaryEdgeTracks.Build(pts).All(t => t.Type == TrackType.ABLine), Is.True);
    }

    [Test]
    public void Round_field_gives_no_edges()
    {
        var pts = Enumerable.Range(0, 360)
            .Select(i => new BoundaryPoint(100 * Math.Cos(i * Math.PI / 180), 100 * Math.Sin(i * Math.PI / 180), 0))
            .ToList();
        Assert.That(BoundaryEdgeTracks.Build(pts), Is.Empty);
    }
}
