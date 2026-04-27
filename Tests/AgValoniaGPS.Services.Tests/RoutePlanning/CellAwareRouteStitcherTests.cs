// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class CellAwareRouteStitcherTests
{
    private const double TurningRadius = 2.0;
    private const double OpWidth = 1.0;
    private const double SweepNorth = 0.0;

    private static List<Vec2> Rect(double minE, double maxE, double minN, double maxN)
        => new() { new(minE, minN), new(maxE, minN), new(maxE, maxN), new(minE, maxN) };

    private static (List<Cell> cells, Dictionary<int, List<GeneratedSwath>> cellSwaths)
        BuildPipeline(List<Vec2> outer, List<List<Vec2>>? topoInners = null,
                      List<List<Vec2>>? localObstacles = null,
                      double swathHeading = Math.PI / 2)
    {
        var cells = BoustrophedonDecomp.Decompose(outer, topoInners ?? new(), SweepNorth);
        if (localObstacles != null) LocalObstacleAttacher.Attach(cells, localObstacles);
        CellCornerClassifier.ClassifyAll(cells, outer, SweepNorth);

        var gen = new RotationalSwathGenerator();
        var cellSwaths = new Dictionary<int, List<GeneratedSwath>>();
        foreach (var cell in cells)
            cellSwaths[cell.Id] = gen.Generate(cell, swathHeading, OpWidth);
        return (cells, cellSwaths);
    }

    [Test]
    public void EmptyInput_ReturnsEmptyPlan()
    {
        var stitcher = new CellAwareRouteStitcher(TurningRadius);
        var plan = stitcher.Stitch(
            new List<Cell>(),
            new Dictionary<int, List<GeneratedSwath>>(),
            new Vec3(0, 0, 0));
        Assert.That(plan.Segments.Count, Is.EqualTo(0));
    }

    [Test]
    public void Rectangle_ProducesSwathsAndTurns()
    {
        // 10×10 rectangle, swaths along east, opWidth 1 → 10 swaths, 9 turns.
        var (cells, cellSwaths) = BuildPipeline(Rect(0, 10, 0, 10));
        var stitcher = new CellAwareRouteStitcher(TurningRadius);

        var plan = stitcher.Stitch(cells, cellSwaths, new Vec3(0, 0, Math.PI / 2));

        Assert.That(plan.SwathCount, Is.EqualTo(10), "expected 10 swath segments");
        Assert.That(plan.TurnCount, Is.EqualTo(9), "expected 9 inter-swath turns");
        Assert.That(plan.TransitCount, Is.EqualTo(0), "single-cell field has no inter-cell transit");
        Assert.That(plan.TotalSwathDistance, Is.EqualTo(100.0).Within(1e-3),
            "10 swaths × 10m each = 100m");
    }

    [Test]
    public void RectangleWithUndrivableHole_SplitSwathsGetSiblingConnectors()
    {
        // 10×10 rect with a 4×2 hole at x∈[3,7], y∈[4,6] as a LOCAL obstacle.
        // Swaths along east. Two swaths cross the hole and split into two
        // segments → two extra Transit (sibling-connector) segments.
        var (cells, cellSwaths) = BuildPipeline(
            Rect(0, 10, 0, 10),
            localObstacles: new List<List<Vec2>> { Rect(3, 7, 4, 6) });

        var stitcher = new CellAwareRouteStitcher(TurningRadius);
        var plan = stitcher.Stitch(cells, cellSwaths, new Vec3(0, 0, Math.PI / 2));

        // Swath count = 10 (uninterrupted) + 2*1 (the two split swaths each
        // contribute 2 segments, so +2 over the unsplit baseline of 10) = 12.
        Assert.That(plan.SwathCount, Is.EqualTo(12));
        // Turns = 9 inter-swath turns. Sibling connectors are TRANSIT, not turns.
        Assert.That(plan.TurnCount, Is.EqualTo(9));
        // Sibling connectors = 2 (one per split swath).
        Assert.That(plan.TransitCount, Is.EqualTo(2));
    }

    [Test]
    public void LShape_TwoCellsWithInterCellTransit()
    {
        // L-shape decomposes into 2 cells. Stitcher visits both with one
        // inter-cell transit between them.
        var l = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10),
        };
        var (cells, cellSwaths) = BuildPipeline(l);
        Assert.That(cells.Count, Is.EqualTo(2), "L-shape decomposes into 2 cells");

        var stitcher = new CellAwareRouteStitcher(TurningRadius);
        var plan = stitcher.Stitch(cells, cellSwaths, new Vec3(0, 0, Math.PI / 2));

        Assert.That(plan.SwathCount, Is.GreaterThan(0));
        Assert.That(plan.TransitCount, Is.EqualTo(1),
            "Two cells should produce exactly one inter-cell transit");
        Assert.That(plan.TotalDistance, Is.GreaterThan(plan.TotalSwathDistance),
            "transit + turns add to the total");
    }

    [Test]
    public void SegmentsAreContiguous_EndOfOneIsStartOfNext()
    {
        var (cells, cellSwaths) = BuildPipeline(Rect(0, 10, 0, 10));
        var stitcher = new CellAwareRouteStitcher(TurningRadius);
        var plan = stitcher.Stitch(cells, cellSwaths, new Vec3(0, 0, Math.PI / 2));

        for (int i = 1; i < plan.Segments.Count; i++)
        {
            var prevEnd = plan.Segments[i - 1].Waypoints[^1];
            var nextStart = plan.Segments[i].Waypoints[0];
            double d = Math.Sqrt(
                (prevEnd.Easting - nextStart.Easting) * (prevEnd.Easting - nextStart.Easting) +
                (prevEnd.Northing - nextStart.Northing) * (prevEnd.Northing - nextStart.Northing));
            Assert.That(d, Is.LessThan(1e-3),
                $"Segment {i} starts at ({nextStart.Easting:F4},{nextStart.Northing:F4}) but " +
                $"segment {i - 1} ended at ({prevEnd.Easting:F4},{prevEnd.Northing:F4}); " +
                $"gap = {d:F6}");
        }
    }

    [Test]
    public void SwathsAreInBoustrophedonOrder_AlternatingDirection()
    {
        var (cells, cellSwaths) = BuildPipeline(Rect(0, 10, 0, 10));
        var stitcher = new CellAwareRouteStitcher(TurningRadius);
        var plan = stitcher.Stitch(cells, cellSwaths, new Vec3(0, 0, Math.PI / 2));

        var swathSegments = plan.Segments.Where(s => s.Type == RouteSegmentType.Swath).ToList();
        Assert.That(swathSegments.Count, Is.EqualTo(10));

        // Adjacent swaths should run in opposite directions (boustrophedon).
        for (int i = 1; i < swathSegments.Count; i++)
        {
            Assert.That(swathSegments[i].IsReverse, Is.Not.EqualTo(swathSegments[i - 1].IsReverse),
                $"Swath {i} should reverse direction relative to swath {i - 1}");
        }
    }

    [Test]
    public void ValidationRejectsNonPositiveTurningRadius()
    {
        Assert.That(() => new CellAwareRouteStitcher(0), Throws.ArgumentException);
        Assert.That(() => new CellAwareRouteStitcher(-1), Throws.ArgumentException);
    }

    [Test]
    public void HeadlandRoutedTransit_TaggedHeadlandRouted()
    {
        // L-shape decomposes into 2 cells. With headland loops supplied, every
        // transit between cells (or between coverage phases) is tagged
        // HeadlandRouted because it rides the loop network.
        var l = new List<Vec2>
        {
            new(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10),
        };
        var (cells, cellSwaths) = BuildPipeline(l);
        var loops = new HeadlandTrackGenerator()
            .Generate(l, new List<List<Vec2>>(), opWidth: OpWidth, passes: 1);
        Assert.That(loops.Count, Is.GreaterThan(0), "L-shape must produce at least one headland loop");

        var stitcher = new CellAwareRouteStitcher(TurningRadius);
        var planWithLoops = stitcher.Stitch(cells, cellSwaths, new Vec3(0, 0, Math.PI / 2), loops);

        var transitsWithLoops = planWithLoops.Segments
            .Where(s => s.Type == RouteSegmentType.Transit && s.TurnPathType == "HeadlandRouted").ToList();
        Assert.That(transitsWithLoops.Count, Is.GreaterThanOrEqualTo(1),
            "At least one transit should be HeadlandRouted (inter-cell or coverage→cell)");
    }

    [Test]
    public void HeadlandCoverage_OuterFirstThenInteriorCells()
    {
        // The current Stitch always emits outer headland coverage FIRST, then
        // interior cells in sequential sweep order. The first Swath in the
        // plan should be a headland loop (long perimeter), and the rest
        // should be interior cell swaths (short length per the field width).
        var outer = Rect(0, 30, 0, 30);
        var (cells, cellSwaths) = BuildPipeline(outer);
        var loops = new HeadlandTrackGenerator()
            .Generate(outer, new List<List<Vec2>>(), opWidth: OpWidth, passes: 1);

        var stitcher = new CellAwareRouteStitcher(TurningRadius);
        var plan = stitcher.Stitch(cells, cellSwaths, new Vec3(0, 0, Math.PI / 2), loops);

        var swaths = plan.Segments.Where(s => s.Type == RouteSegmentType.Swath).ToList();
        Assert.That(swaths.Count, Is.GreaterThan(loops.Count),
            "Plan should include both headland passes and interior swaths");
        Assert.That(swaths[0].Length, Is.GreaterThan(50.0),
            "First Swath should be the outer headland loop (~120m for a 30×30 field)");
        Assert.That(swaths[^1].Length, Is.LessThan(50.0),
            "Last Swath should be an interior cell swath (≤ 30m on a 30×30 field)");
    }

    [Test]
    public void RectangleWithExpandedHole_TransitsRideInnerOrOuterLoop()
    {
        // 60x60 outer with a 10x10 pond. HeadlandDistance large enough that
        // the Dubins splices fit comfortably inside the buffer zone (avoids
        // splice-overshoot artifacts that only appear at unrealistic
        // R-to-buffer ratios).
        const double R = 0.5;
        const double w = 2.0;          // opWidth
        const int passes = 3;          // HeadlandDistance = passes * w = 6m
        const double headlandDist = passes * w;

        var outer = Rect(0, 60, 0, 60);
        var pond = Rect(25, 35, 25, 35);
        var expandedPond = new Services.Geometry.PolygonOffsetService()
            .CreateOutwardOffset(pond, headlandDist)!;

        var cells = BoustrophedonDecomp.Decompose(outer,
            new List<List<Vec2>> { expandedPond }, SweepNorth);
        CellCornerClassifier.ClassifyAll(cells, outer,
            new List<List<Vec2>> { expandedPond }, SweepNorth);

        var gen = new RotationalSwathGenerator();
        var cellSwaths = new Dictionary<int, List<GeneratedSwath>>();
        foreach (var c in cells)
            cellSwaths[c.Id] = gen.Generate(c, swathHeading: Math.PI / 2, w);

        var loops = new HeadlandTrackGenerator()
            .Generate(outer, new List<List<Vec2>> { pond }, opWidth: w, passes: passes);
        Assert.That(loops.Any(l => l.Kind == HeadlandLoopKind.Outer), "must have outer loops");
        Assert.That(loops.Any(l => l.Kind == HeadlandLoopKind.Inner), "must have inner loops");

        var stitcher = new CellAwareRouteStitcher(R);
        var plan = stitcher.Stitch(cells, cellSwaths, new Vec3(0, 0, Math.PI / 2), loops);

        Assert.That(plan.SwathCount, Is.GreaterThan(0));
        var transits = plan.Segments.Where(s => s.Type == RouteSegmentType.Transit
            && s.TurnPathType == "HeadlandRouted").ToList();
        Assert.That(transits.Count, Is.GreaterThan(0),
            "At least one inter-cell transit should ride the headland loops");

        // No transit waypoint should land inside the original pond polygon
        // (would mean we cut through the cordoned obstacle).
        foreach (var t in transits)
        {
            foreach (var wp in t.Waypoints)
            {
                bool insidePond = AgValoniaGPS.Models.Base.GeometryMath.IsPointInPolygon(
                    pond, new Vec2(wp.Easting, wp.Northing));
                Assert.That(insidePond, Is.False,
                    $"Transit waypoint ({wp.Easting:F2},{wp.Northing:F2}) is inside the pond polygon");
            }
        }
    }
}
