// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;
using AgValoniaGPS.Models.Track;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.RoutePlanning;
using AgValoniaGPS.Services.Track;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class RouteStitchingServiceTests
{
    private RouteStitchingService _service = null!;

    private static BoundaryPolygon MakeRectBoundary(double minE, double maxE, double minN, double maxN)
    {
        var bp = new BoundaryPolygon();
        bp.Points.Add(new BoundaryPoint { Easting = minE, Northing = minN });
        bp.Points.Add(new BoundaryPoint { Easting = maxE, Northing = minN });
        bp.Points.Add(new BoundaryPoint { Easting = maxE, Northing = maxN });
        bp.Points.Add(new BoundaryPoint { Easting = minE, Northing = maxN });
        bp.UpdateBounds();
        return bp;
    }

    private static Models.Track.Track MakeSwath(double easting, double startN, double endN, double heading = 0)
    {
        return Models.Track.Track.FromCurve(
            $"Swath E={easting}",
            new List<Vec3>
            {
                new Vec3(easting, startN, heading),
                new Vec3(easting, endN, heading)
            });
    }

    [SetUp]
    public void SetUp()
    {
        var turnService = new TurnPathService();
        _service = new RouteStitchingService(turnService);
    }

    [Test]
    public void ThreeSwaths_ProducesFiveSegments()
    {
        // 3 swaths → swath, turn, swath, turn, swath = 5 segments
        var swaths = new List<Models.Track.Track>
        {
            MakeSwath(0, -200, 200),
            MakeSwath(10, -200, 200),
            MakeSwath(20, -200, 200),
        };

        var config = new RouteStitchConfig
        {
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = MakeRectBoundary(-50, 70, -200, 260),
            ReferenceHeading = 0,
            Pattern = SwathPattern.Boustrophedon,
        };

        var plan = _service.StitchRoute(swaths, config);

        Assert.That(plan.Segments.Count, Is.EqualTo(5));
        Assert.That(plan.SwathCount, Is.EqualTo(3));
        Assert.That(plan.TurnCount, Is.EqualTo(2));
    }

    [Test]
    public void SingleSwath_NoTurns()
    {
        var swaths = new List<Models.Track.Track>
        {
            MakeSwath(0, -200, 200),
        };

        var config = new RouteStitchConfig
        {
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = MakeRectBoundary(-50, 50, -200, 260),
            ReferenceHeading = 0,
            Pattern = SwathPattern.Boustrophedon,
        };

        var plan = _service.StitchRoute(swaths, config);

        Assert.That(plan.Segments.Count, Is.EqualTo(1));
        Assert.That(plan.SwathCount, Is.EqualTo(1));
        Assert.That(plan.TurnCount, Is.EqualTo(0));
    }

    [Test]
    public void AlternatingDirection_OddSwathsReversed()
    {
        var swaths = new List<Models.Track.Track>
        {
            MakeSwath(0, -200, 200),
            MakeSwath(10, -200, 200),
            MakeSwath(20, -200, 200),
        };

        var config = new RouteStitchConfig
        {
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = MakeRectBoundary(-50, 70, -200, 260),
            ReferenceHeading = 0,
            Pattern = SwathPattern.Boustrophedon,
        };

        var plan = _service.StitchRoute(swaths, config);

        var swathSegments = plan.Segments.Where(s => s.Type == RouteSegmentType.Swath).ToList();
        Assert.That(swathSegments[0].IsReverse, Is.False, "First swath should be forward");
        Assert.That(swathSegments[1].IsReverse, Is.True, "Second swath should be reversed");
        Assert.That(swathSegments[2].IsReverse, Is.False, "Third swath should be forward");
    }

    [Test]
    public void TotalDistances_ComputedCorrectly()
    {
        var swaths = new List<Models.Track.Track>
        {
            MakeSwath(0, -100, 100),  // 200m
            MakeSwath(10, -100, 100), // 200m
        };

        var config = new RouteStitchConfig
        {
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = MakeRectBoundary(-50, 60, -100, 160),
            ReferenceHeading = 0,
            Pattern = SwathPattern.Boustrophedon,
        };

        var plan = _service.StitchRoute(swaths, config);

        Assert.That(plan.TotalSwathDistance, Is.EqualTo(400).Within(1), "Two 200m swaths");
        Assert.That(plan.TotalTurnDistance, Is.GreaterThan(0), "Should have turn distance");
        Assert.That(plan.TotalDistance, Is.GreaterThan(plan.TotalSwathDistance), "Total includes turns");
    }

    [Test]
    public void ValidTurns_InWideField()
    {
        // Boundary must extend beyond swath endpoints in both directions
        // to give room for turn arcs at both ends
        var swaths = new List<Models.Track.Track>
        {
            MakeSwath(0, -200, 200),
            MakeSwath(10, -200, 200),
            MakeSwath(20, -200, 200),
        };

        var config = new RouteStitchConfig
        {
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = MakeRectBoundary(-50, 70, -260, 260),
            ReferenceHeading = 0,
            Pattern = SwathPattern.Boustrophedon,
        };

        var plan = _service.StitchRoute(swaths, config);

        Assert.That(plan.InvalidTurnCount, Is.EqualTo(0), "All turns should be valid in wide field");
    }

    [Test]
    public void EmptySwathList_ReturnsEmptyPlan()
    {
        var config = new RouteStitchConfig
        {
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = MakeRectBoundary(-50, 50, -200, 260),
            ReferenceHeading = 0,
            Pattern = SwathPattern.Boustrophedon,
        };

        var plan = _service.StitchRoute(new List<Models.Track.Track>(), config);

        Assert.That(plan.Segments, Is.Empty);
        Assert.That(plan.SwathCount, Is.EqualTo(0));
        Assert.That(plan.TurnCount, Is.EqualTo(0));
    }

    [Test]
    public void PatternStoredOnPlan()
    {
        var swaths = new List<Models.Track.Track> { MakeSwath(0, -100, 100) };

        var config = new RouteStitchConfig
        {
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = MakeRectBoundary(-50, 50, -100, 160),
            ReferenceHeading = 0,
            Pattern = SwathPattern.Snake,
        };

        var plan = _service.StitchRoute(swaths, config);

        Assert.That(plan.Pattern, Is.EqualTo("Snake"));
    }
}
