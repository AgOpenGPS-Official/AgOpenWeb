// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.Services.RoutePlanning;

namespace AgValoniaGPS.Services.Tests.RoutePlanning;

[TestFixture]
public class TurnPathServiceTests
{
    private TurnPathService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new TurnPathService();
    }

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

    [Test]
    public void RectangularField_TurnStaysInsideBoundary()
    {
        // Wide rectangular field — plenty of room for turns
        var boundary = MakeRectBoundary(-100, 100, -300, 300);
        double heading = 0; // North

        var input = new TurnPathInput
        {
            ExitPoint = new Vec3(0, 200, heading),
            ExitHeading = heading,
            EntryPoint = new Vec3(10, 200, heading + Math.PI),
            EntryHeading = heading + Math.PI,
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = boundary,
        };

        var result = _service.GenerateTurn(input);

        Assert.That(result.IsValid, Is.True, "Turn should fit inside wide rectangular field");
        Assert.That(result.Waypoints.Count, Is.GreaterThan(2));
        Assert.That(result.Length, Is.GreaterThan(0));
        Assert.That(result.PathType, Is.Not.EqualTo("none"));
    }

    [Test]
    public void NarrowHeadland_ReturnsInvalidTurn()
    {
        // Boundary barely extends past the swath endpoints — no room for any Dubins turn
        var boundary = MakeRectBoundary(-5, 15, -200, 203);
        double heading = 0;

        var input = new TurnPathInput
        {
            ExitPoint = new Vec3(0, 200, heading),
            ExitHeading = heading,
            EntryPoint = new Vec3(10, 200, heading + Math.PI),
            EntryHeading = heading + Math.PI,
            TurningRadius = 8.0,
            HeadlandWidth = 3.0,
            Boundary = boundary,
        };

        var result = _service.GenerateTurn(input);

        // With only 3m past the swath end and 8m turning radius, no Dubins path can fit
        Assert.That(result.IsValid, Is.False, "Turn should not fit in narrow headland");
        // Should still return a path (best-effort fallback)
        Assert.That(result.Waypoints.Count, Is.GreaterThan(0));
    }

    [Test]
    public void AdjacentSwaths_Boustrophedon_ProducesUTurn()
    {
        // Adjacent swaths 10m apart — tight 180-degree U-turn
        var boundary = MakeRectBoundary(-50, 50, -200, 260);
        double heading = 0;

        var input = new TurnPathInput
        {
            ExitPoint = new Vec3(0, 200, heading),
            ExitHeading = heading,
            EntryPoint = new Vec3(10, 200, heading + Math.PI),
            EntryHeading = heading + Math.PI,
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = boundary,
        };

        var result = _service.GenerateTurn(input);

        Assert.That(result.IsValid, Is.True);
        // A U-turn for 10m spacing should be roughly: leg + semicircle + leg
        // Semicircle circumference / 2 ≈ π * r ≈ 25m, plus legs
        Assert.That(result.Length, Is.GreaterThan(20));
    }

    [Test]
    public void WideSkip_SnakePattern_LongerTurn()
    {
        // Swaths 20m apart (skip-one pattern) — wider turn
        var boundary = MakeRectBoundary(-50, 70, -200, 260);
        double heading = 0;

        var input = new TurnPathInput
        {
            ExitPoint = new Vec3(0, 200, heading),
            ExitHeading = heading,
            EntryPoint = new Vec3(20, 200, heading + Math.PI),
            EntryHeading = heading + Math.PI,
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = boundary,
        };

        var result = _service.GenerateTurn(input);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Length, Is.GreaterThan(25), "Wider spacing should produce longer turn");
    }

    [Test]
    public void AllWaypointsInsideBoundary_WhenValid()
    {
        var boundary = MakeRectBoundary(-50, 50, -200, 260);
        double heading = 0;

        var input = new TurnPathInput
        {
            ExitPoint = new Vec3(0, 200, heading),
            ExitHeading = heading,
            EntryPoint = new Vec3(10, 200, heading + Math.PI),
            EntryHeading = heading + Math.PI,
            TurningRadius = 8.0,
            HeadlandWidth = 30.0,
            Boundary = boundary,
        };

        var result = _service.GenerateTurn(input);

        Assert.That(result.IsValid, Is.True);

        // Verify every waypoint is inside the boundary
        var boundaryVec2 = boundary.Points
            .Select(p => new Vec2(p.Easting, p.Northing))
            .ToList();
        foreach (var pt in result.Waypoints)
        {
            Assert.That(
                GeometryMath.IsPointInPolygon(boundaryVec2, new Vec2(pt.Easting, pt.Northing)),
                Is.True,
                $"Point ({pt.Easting:F1}, {pt.Northing:F1}) should be inside boundary");
        }
    }
}
