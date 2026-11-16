// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// The obstacle shapes' sums that run on the host while the operator waits or every cycle: an
/// obstacle's size in the list (<see cref="BoundaryPolygon.LongestSpan"/>), its outline's
/// fingerprint (<see cref="BoundaryPolygon.OutlineStamp"/>), a circle's polygon recognised only
/// while it is that circle's (<see cref="ObstacleCircle.Matches"/>) and the circle round a big
/// drawn shape (<see cref="ObstacleCircle.Covering"/>).
/// </summary>
[TestFixture]
public class ObstacleShapeReviewTests
{
    private static BoundaryPolygon Polygon(IEnumerable<(double E, double N)> points)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in points) p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private static double EveryPair(BoundaryPolygon p)
    {
        double best = 0;
        foreach (var a in p.Points)
            foreach (var b in p.Points)
                best = Math.Max(best, Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing)));
        return best;
    }

    [Test]
    public void LongestSpan_IsTheDistanceBetweenTheTwoFarthestPoints()
    {
        var random = new Random(7);
        for (int run = 0; run < 200; run++)
        {
            int n = 3 + random.Next(60);
            var p = Polygon(Enumerable.Range(0, n).Select(_ => (random.NextDouble() * 40 - 20, random.NextDouble() * 15)));
            Assert.That(p.LongestSpan(), Is.EqualTo(EveryPair(p)).Within(1e-9), $"run {run}");
        }
        Assert.That(Polygon(new[] { (0.0, 0.0), (30.0, 0.0), (30.0, 30.0), (0.0, 30.0) }).LongestSpan(), Is.EqualTo(30 * Math.Sqrt(2)).Within(1e-9));
        Assert.That(Polygon(new[] { (0.0, 0.0), (1.0, 1.0), (2.0, 2.0), (2.0, 2.0) }).LongestSpan(), Is.EqualTo(2 * Math.Sqrt(2)).Within(1e-9), "in a line");
        Assert.That(Polygon(new[] { (5.0, 5.0), (5.0, 5.0) }).LongestSpan(), Is.EqualTo(0));
        var circle = new BoundaryPolygon();
        circle.SetCircle(new ObstacleCircle(3, 4, 6));
        Assert.That(circle.LongestSpan(), Is.EqualTo(EveryPair(circle)).Within(1e-9));
    }

    [Test]
    public void LongestSpan_OfARecordedPond_IsQuick()
    {
        // 40 000 points a few centimetres apart round a 400 m pond: every pair was 800 million sums.
        var pond = Polygon(Enumerable.Range(0, 40000).Select(i =>
        {
            double a = 2 * Math.PI * i / 40000;
            return (200 * Math.Cos(a) + 3 * Math.Sin(7 * a), 120 * Math.Sin(a));
        }));
        var clock = Stopwatch.StartNew();
        double span = pond.LongestSpan();
        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(2));
        Assert.That(span, Is.GreaterThan(399).And.LessThan(407));
    }

    [Test]
    public void OutlineStamp_SeesEveryCorner_AndOnlyTheOutline()
    {
        var a = Polygon(new[] { (30.0, 40.0), (40.0, 40.0), (50.0, 40.0), (50.0, 60.0), (40.0, 60.0), (30.0, 60.0) });
        var same = Polygon(new[] { (30.0, 40.0), (40.0, 40.0), (50.0, 40.0), (50.0, 60.0), (40.0, 60.0), (30.0, 60.0) });
        Assert.That(same.OutlineStamp, Is.EqualTo(a.OutlineStamp));
        long before = a.OutlineStamp;

        // Corner 1: neither the first nor the middle one, the two read before.
        a.Points[1] = new BoundaryPoint(40, 38, 0);
        a.UpdateBounds();
        Assert.That(a.OutlineStamp, Is.Not.EqualTo(before));

        a.Points[1] = new BoundaryPoint(40, 40, 0);
        a.UpdateBounds();
        Assert.That(a.OutlineStamp, Is.EqualTo(before), "back where it was");

        a.Points = a.Points.Take(5).ToList(); // a new list: no UpdateBounds needed
        Assert.That(a.OutlineStamp, Is.Not.EqualTo(before));
    }

    [Test]
    public void Matches_TheCirclesPolygon_FromAnyCorner_EitherWay()
    {
        var c = new ObstacleCircle(10, -20, 6);
        var pts = c.ToPoints();
        Assert.That(c.Matches(pts), Is.True);
        var turned = pts.Skip(5).Concat(pts.Take(5)).ToList();
        Assert.That(c.Matches(turned), Is.True, "starting at another corner");
        var reversed = Enumerable.Reverse(pts).ToList();
        Assert.That(c.Matches(reversed), Is.True, "clockwise, as a file may store it");
    }

    [Test]
    public void Matches_NotAPolygonWhoseCornersMovedRoundTheCircle()
    {
        var c = new ObstacleCircle(10, -20, 6);
        double r = c.CornerRadius;

        // Every corner still at the corner distance from the centre: the old test saw a circle.
        var slid = c.ToPoints();
        double a = 2 * Math.PI * 3.5 / ObstacleCircle.Segments;
        slid[3] = new BoundaryPoint(10 + r * Math.Cos(a), -20 + r * Math.Sin(a), 0);
        Assert.That(c.Matches(slid), Is.False, "a corner moved half a step round");

        var swapped = c.ToPoints();
        (swapped[3], swapped[4]) = (swapped[4], swapped[3]);
        Assert.That(c.Matches(swapped), Is.False, "two corners swapped: a crossed outline");

        var spun = c.ToPoints().Select(p =>
        {
            double t = Math.Atan2(p.Northing + 20, p.Easting - 10) + 0.1;
            return new BoundaryPoint(10 + r * Math.Cos(t), -20 + r * Math.Sin(t), 0);
        }).ToList();
        Assert.That(c.Matches(spun), Is.False, "every corner turned round the centre");
    }

    [Test]
    public void Covering_OfABigShapeInTheOrderDrawn_IsQuick_AndTheSameCircle()
    {
        // Round and round, in the order recorded: the incremental search without a shuffle
        // could do about n³ sums here.
        var ring = Enumerable.Range(0, 20000).Select(i =>
        {
            double t = 2 * Math.PI * i / 20000;
            return new BoundaryPoint(50 + 30 * Math.Cos(t), 20 + 30 * Math.Sin(t), 0);
        }).ToList();
        var clock = Stopwatch.StartNew();
        var circle = ObstacleCircle.Covering(ring);
        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(2));
        Assert.That(circle.Diameter, Is.EqualTo(60).Within(0.1));
        Assert.That(circle.CenterEasting, Is.EqualTo(50).Within(0.01));
        Assert.That(circle.CenterNorthing, Is.EqualTo(20).Within(0.01));

        var reversed = ObstacleCircle.Covering(Enumerable.Reverse(ring).ToList());
        Assert.That(reversed.Diameter, Is.EqualTo(circle.Diameter));
        Assert.That(reversed.CenterEasting, Is.EqualTo(circle.CenterEasting).Within(1e-6));
    }
}
