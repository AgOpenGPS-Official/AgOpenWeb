// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The turn from a pass along a fence, in towards the field (field job C14, Olival, 2026-11-15):
/// pass -8 runs 4 m from a fence parallel to it for 200 m, the turn line 2 m inside the fence.
/// The sagitta turn's lead-in swings 4 m to the fence side first, so it fitted nowhere along the
/// pass, and the operator was told "No U-turn fits inside the boundary" with the whole field left.
/// AgOpenGPS's omega turn needs 2.0 m on that side; with 1.9 m it can't turn there either, and
/// the message now says by how much the geometry refuses. With the U-turn distance at 1 m (2.9 m
/// of room) the omega is built where the sagitta still can't be.
/// </summary>
[TestFixture, NonParallelizable]
public class EdgePassTurnTests
{
    private sealed class OutLogger<T> : ILogger<T>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => true;
        public void Log<TState>(LogLevel l, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
        {
            string line = f(state, ex);
            Lines.Add(line);
            TestContext.Out.WriteLine(line);
        }
    }

    private static OutLogger<YouTurnCreationService> _log = new();

    // Olival (the job harness's field, in local metres about its origin).
    private static readonly List<Vec2> Olival = new()
    {
        new(-6.1,161.5), new(5.5,146.3), new(19.8,119.2), new(31.7,86.5), new(12.4,133.0), new(10.8,131.4), new(4.5,114.4), new(14.5,91.1), new(24.2,85.1), new(29.7,84.0), new(32.4,84.7), new(44.5,51.6), new(77.1,-31.2), new(105.7,-104.6), new(82.2,-124.1), new(63.4,-138.8), new(38.5,-154.2), new(38.3,-180.8), new(41.3,-196.2), new(44.3,-229.5), new(-20.4,-222.4), new(-23.0,-201.9), new(-20.2,-187.4), new(-23.2,-147.9), new(-29.9,-97.0), new(-35.9,-59.8), new(-43.3,-21.8), new(-49.8,16.8), new(-58.5,38.5), new(-63.2,50.9), new(-60.8,58.2), new(-66.6,66.3), new(-74.9,107.2), new(-86.5,149.1), new(-47.7,157.8), new(-10.7,164.0),
    };

    /// <summary>On Olival, the tractor abreast of the AB line's middle: pass -8 heading NW has its end
    /// 60 m ahead, pass -6 heading SE 146 m.</summary>
    private static YouTurnCreationService.TurnPathResult Plan(int pass, bool sameWay, bool left, double distanceFromBoundary)
        => Plan(Olival, new Vec3(-56.4, 137.6, 0), new Vec3(45.8, -128.9, 0), pass, sameWay, left, distanceFromBoundary,
            sameWay ? 146.3 : 60.3);

    private static YouTurnCreationService.TurnPathResult Plan(List<Vec2> fence, Vec3 a, Vec3 b, int pass, bool sameWay, bool left,
        double distanceFromBoundary, double distanceToTurnLine)
    {
        _log = new OutLogger<YouTurnCreationService>();
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 800);
        c.Guidance.UTurnDistanceFromBoundary = distanceFromBoundary;
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 16.0;
        var offset = new PolygonOffsetService();
        var creation = new YouTurnCreationService(_log, offset, c);

        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(fence, distanceFromBoundary)!);
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", a, b);
        double abHeading = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
        if (abHeading < 0) abHeading += 2 * Math.PI;

        // The tractor on the pass abreast of the line's middle, heading along it or back.
        double perp = abHeading + Math.PI / 2;
        double away = AgOpenWeb.Models.Track.PassGeometry.Offset(track, pass, 8.0);
        double travel = sameWay ? abHeading : abHeading - Math.PI;
        if (travel < 0) travel += 2 * Math.PI;
        var pos = new Position
        {
            Easting = (a.Easting + b.Easting) / 2 + Math.Sin(perp) * away,
            Northing = (a.Northing + b.Northing) / 2 + Math.Cos(perp) * away,
            Heading = travel,
        };

        var guidance = new GuidanceWorkingState { IsHeadingSameWay = sameWay, HowManyPathsAway = pass };
        var turn = new YouTurnWorkingState { IsTurnLeft = left, DistanceToHeadland = distanceToTurnLine };
        return creation.CreateTurnPath(pos, track, travel, abHeading, boundary, turnLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 16, headlandDistance: distanceToTurnLine);
    }

    [Test]
    public void Along_a_fence_with_2m_of_room_no_turn_fits_and_the_shortfall_is_told()
    {
        var r = Plan(pass: -8, sameWay: false, left: true, distanceFromBoundary: 2.0);
        Assert.Multiple(() =>
        {
            Assert.That(r.Path, Is.Null, "no turn of radius 8 fits between pass -8 and the fence beside it");
            Assert.That(r.UsedFallback, Is.True);
            Assert.That(r.OutsideTurnLineBy, Is.Not.Null, "the geometry refused it, and says by how much");
            // The omega's bulb swings about 4.3 m to the fence side at this radius and pass spacing,
            // the sagitta's about 7.3 m; the pass has 1.9 m.
            Assert.That(r.OutsideTurnLineBy!.Value, Is.GreaterThan(2.0).And.LessThan(3.0), "what the omega, the closer of the two, lacked");
            Assert.That(_log.Lines, Has.Some.Contains("the omega turn instead"), "the sagitta fitted nowhere, so the omega was tried");
        });
    }

    // Pass 0 (its centre 4 m right of the AB line) 6 m from a fence that runs along it (a 12 m
    // implement's edge pass), the turn line 1 m in: 5 m of room on the fence side, which the omega's
    // bulb (about 4.5 m at this radius and pass spacing) has and the sagitta's lead-in (about 8 m) hasn't.
    private static readonly List<Vec2> Strip = new() { new(-100, -150), new(10, -150), new(10, 150), new(-100, 150) };

    [Test]
    public void Along_a_fence_with_5m_of_room_the_omega_turn_is_built_where_the_sagitta_cannot_be()
    {
        var r = Plan(Strip, new Vec3(0, -200, 0), new Vec3(0, 200, 0), pass: 0, sameWay: true, left: true, distanceFromBoundary: 1.0, distanceToTurnLine: 149);
        Assert.Multiple(() =>
        {
            Assert.That(r.Path, Is.Not.Null);
            Assert.That(r.UsedFallback, Is.False);
            Assert.That(_log.Lines, Has.Some.Contains("the omega turn instead"), "the sagitta fitted nowhere, so the omega was tried");
            Assert.That(r.Path!.Max(p => p.Easting), Is.LessThan(9.0), "the omega stays inside the turn line, 5 m from the pass");
        });
    }

    [Test]
    public void Along_a_fence_with_3m_of_room_no_turn_fits_and_the_shortfall_is_told()
    {
        var r = Plan(Strip, new Vec3(0, -200, 0), new Vec3(0, 200, 0), pass: 0, sameWay: true, left: true, distanceFromBoundary: 3.0, distanceToTurnLine: 147);
        Assert.That(r.Path, Is.Null);
        Assert.That(r.OutsideTurnLineBy, Is.Not.Null.And.GreaterThan(0.5).And.LessThan(2.5), "the omega's bulb against 3 m of room");
    }

    [Test]
    public void Towards_the_fence_no_turn_fits_either()
    {
        var r = Plan(pass: -8, sameWay: false, left: false, distanceFromBoundary: 2.0);
        Assert.That(r.Path, Is.Null);
        Assert.That(r.OutsideTurnLineBy, Is.Not.Null);
    }

    [Test]
    public void One_pass_in_the_sagitta_turn_is_built()
    {
        // C14's first turn (pass -6, the same way, left): room on both sides.
        var r = Plan(pass: -6, sameWay: true, left: true, distanceFromBoundary: 2.0);
        Assert.That(r.Path, Is.Not.Null);
        Assert.That(r.UsedFallback, Is.False);
        Assert.That(r.OutsideTurnLineBy, Is.Null);
    }

    [Test]
    public void The_message_says_by_how_much()
    {
        Assert.That(YouTurnStateMachine.NoTurnFitsMessage(0.12, 2.0),
            Is.EqualTo("No U-turn fits inside the boundary at this end: the closest would come 0.1 m nearer the boundary than the U-turn distance of 2.0 m allows - take over manually"));
    }
}
