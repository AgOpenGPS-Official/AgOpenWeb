using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Tests;

[TestFixture]
public class BoundaryResolutionTests
{
    // Build a closed ring sampled densely along a shape function.
    private static List<BoundaryPoint> Sample(int count, Func<double, (double e, double n)> shape)
    {
        var pts = new List<BoundaryPoint>(count);
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / count;
            var (e, n) = shape(t);
            pts.Add(new BoundaryPoint(e, n, 0));
        }
        return pts;
    }

    [Test]
    public void Normalize_CollapsesCollinearRectangle_ToCorners()
    {
        // A 200m x 100m rectangle sampled every 1m (~600 points) should collapse
        // to roughly its 4 corners.
        var pts = new List<BoundaryPoint>();
        void Edge(double x0, double y0, double x1, double y1)
        {
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int steps = (int)len;
            for (int i = 0; i < steps; i++)
            {
                double t = (double)i / steps;
                pts.Add(new BoundaryPoint(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, 0));
            }
        }
        Edge(0, 0, 200, 0);
        Edge(200, 0, 200, 100);
        Edge(200, 100, 0, 100);
        Edge(0, 100, 0, 0);

        int before = pts.Count;
        // maxGap large so densify doesn't re-add points on the straight edges.
        var result = BoundaryResolution.Normalize(pts, toleranceMeters: 0.1, maxGapMeters: 10000);

        Assert.That(before, Is.GreaterThan(500));
        // 4 corners (seam may add 1-2); allow a small margin.
        Assert.That(result.Count, Is.LessThanOrEqualTo(8),
            $"rectangle should collapse to ~4 corners, got {result.Count}");
        Assert.That(result.Count, Is.GreaterThanOrEqualTo(4));
    }

    [Test]
    public void Normalize_PreservesCircleShape_WithinTolerance()
    {
        // A 100m-radius circle sampled at 1m spacing (~628 points). After normalize,
        // every original point must stay within a small distance of the simplified ring.
        var pts = Sample(628, t =>
        {
            double a = t * 2 * Math.PI;
            return (100 * Math.Cos(a), 100 * Math.Sin(a));
        });

        var result = BoundaryResolution.Normalize(pts, toleranceMeters: 0.1, maxGapMeters: 10000);

        Assert.That(result.Count, Is.LessThan(pts.Count));
        Assert.That(result.Count, Is.GreaterThan(8), "a circle must keep enough points to stay round");

        // Max deviation of original points from the simplified polyline stays bounded.
        double maxDev = pts.Max(p => MinDistToRing(p, result));
        Assert.That(maxDev, Is.LessThan(0.5),
            $"simplified circle deviates {maxDev:F3} m from original");
    }

    [Test]
    public void Normalize_DensifiesLongStraightEdge_ToMaxGap()
    {
        // Triangle with a 1000m edge; with a 50m gap cap that edge must gain points.
        var pts = new List<BoundaryPoint>
        {
            new(0, 0, 0),
            new(1000, 0, 0),
            new(500, 300, 0),
        };

        var result = BoundaryResolution.Normalize(pts, toleranceMeters: 0.1, maxGapMeters: 50);

        // No segment in the closed result should exceed the max gap (plus epsilon).
        for (int i = 0; i < result.Count; i++)
        {
            var a = result[i];
            var b = result[(i + 1) % result.Count];
            double d = Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) +
                                 (a.Northing - b.Northing) * (a.Northing - b.Northing));
            Assert.That(d, Is.LessThanOrEqualTo(50.0 + 1e-6), $"segment {i} length {d:F2} exceeds max gap");
        }
    }

    [Test]
    public void Normalize_RecomputesHeadings()
    {
        // Points along +East: heading should be atan2(dE, dN) = atan2(1,0) = PI/2.
        var pts = new List<BoundaryPoint>
        {
            new(0, 0, 0), new(10, 0, 0), new(20, 0, 0),
            new(20, 10, 0), new(0, 10, 0),
        };

        var result = BoundaryResolution.Normalize(pts, toleranceMeters: 0.1, maxGapMeters: 10000);

        // First point heads east toward the next.
        Assert.That(result[0].Heading, Is.EqualTo(Math.PI / 2).Within(1e-6));
    }

    [Test]
    public void Normalize_TinyInput_ReturnedUnchanged()
    {
        var pts = new List<BoundaryPoint> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) };
        var result = BoundaryResolution.Normalize(pts);
        Assert.That(result.Count, Is.EqualTo(3));
    }

    private static double MinDistToRing(BoundaryPoint p, List<BoundaryPoint> ring)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            best = Math.Min(best, Math.Sqrt(SegDistSq(p, a, b)));
        }
        return best;
    }

    private static double SegDistSq(BoundaryPoint p, BoundaryPoint a, BoundaryPoint b)
    {
        double dx = b.Easting - a.Easting, dy = b.Northing - a.Northing;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12)
            return (p.Easting - a.Easting) * (p.Easting - a.Easting) + (p.Northing - a.Northing) * (p.Northing - a.Northing);
        double t = ((p.Easting - a.Easting) * dx + (p.Northing - a.Northing) * dy) / lenSq;
        t = Math.Clamp(t, 0, 1);
        double cx = a.Easting + t * dx, cy = a.Northing + t * dy;
        return (p.Easting - cx) * (p.Easting - cx) + (p.Northing - cy) * (p.Northing - cy);
    }
}
