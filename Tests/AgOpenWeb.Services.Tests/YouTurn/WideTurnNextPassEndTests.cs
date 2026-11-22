// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The wide turn (rows wider than twice the turn radius) runs along the turn line from the end of
/// the tractor's pass to the end of the next pass, so where the next pass "ends" decides the turn.
/// <para>
/// With a large distance from boundary the next pass runs outside the turn line, and inside the
/// fence, wherever the fence comes in beside it. The tractor's own pass is driven through such a
/// stretch (<see cref="PassEnds.Find"/>); the next pass was taken to end at the first one, walking
/// from abreast of the tractor, and the turn ran along the turn line all the way back to it: on a
/// tester's field (an 8 m front boom, radius 5.4 m, 6.3 m from the boundary, one row skipped) 300 m
/// round a corner, refused as malformed with the next pass's real end 16 m from the turn's start.
/// </para>
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class WideTurnNextPassEndTests
{
    private const double FenceNorth = 100;
    private const double FromBoundary = 6.3;

    // The west fence comes in to x = -17 from y = -15 to 15, with 45° sides: the next pass
    // (x = -12) runs past it 5 m from the fence, outside the turn line and inside the field.
    private static List<Vec2> FenceWithBulge(bool bulge) => bulge
        ? new() { new(-60, -100), new(60, -100), new(60, FenceNorth), new(-60, FenceNorth), new(-60, 58), new(-17, 15), new(-17, -15), new(-60, -58) }
        : new() { new(-60, -100), new(60, -100), new(60, FenceNorth), new(-60, FenceNorth) };

    private static YouTurnCreationService NewCreation()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        // A front sprayer: an 8 m boom fixed 4.7 m ahead of the pivot.
        const double width = 8;
        c.Vehicle.Wheelbase = 2.9;
        c.Tool.Width = width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.Vehicle.ThreePointHitchLength = c.Vehicle.FrontHitchLength = 4.7;
        c.Tool.WorkingPointFromHitch = 0;
        c.Tool.IsToolFrontFixed = true;
        c.Tool.IsToolRearFixed = false;
        c.Tool.IsToolTrailing = false;
        c.Tool.IsToolTBT = false;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(width * 100));
        c.Guidance.UTurnDistanceFromBoundary = FromBoundary;
        c.Guidance.UTurnRadius = 5.4;
        c.Guidance.UTurnExtension = 11.0;
        c.Guidance.UTurnStyle = 0;
        return new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);
    }

    private static Boundary Field(List<Vec2> fence, bool hard) => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            IsHard = hard,
        },
    };

    /// <summary>Pass 0 (x = 4) north from y = -70, one row skipped: the next pass is x = -12, to the left.</summary>
    private static YouTurnCreationService.TurnPathResult TurnFromTheSouth(Boundary field, double passEndNorth)
    {
        const double y = -70;
        var creation = NewCreation();
        var turnLine = new PolygonOffsetService().CreateInwardOffset(
            field.OuterBoundary!.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList(), FromBoundary)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 400, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = passEndNorth - FromBoundary - y };
        return creation.CreateTurnPath(new Position { Easting = 4, Northing = y, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, field, turnLine, guidance, turn,
            uTurnSkipRows: 1, headlandCalculatedWidth: 12, headlandDistance: passEndNorth - FromBoundary - y);
    }

    private static double Length(IReadOnlyList<Vec3> path)
    {
        double length = 0;
        for (int i = 1; i < path.Count; i++)
            length += Math.Sqrt(GeometryMath.DistanceSquared(path[i - 1], path[i]));
        return length;
    }

    [TestCase(true, TestName = "The next pass is not ended where it runs past a bulge of the fence (hard fence)")]
    [TestCase(false, TestName = "The next pass is not ended where it runs past a bulge of the fence (soft boundary)")]
    public void The_next_pass_is_driven_past_a_bulge_of_the_fence(bool hard)
    {
        var turnLine = new PolygonOffsetService().CreateInwardOffset(FenceWithBulge(bulge: true), FromBoundary)!;
        Assert.Multiple(() =>
        {
            Assert.That(GeometryMath.IsPointInPolygon(turnLine, new Vec2(-12, 0)), Is.False, "the scenario: beside the bulge the next pass is outside the turn line");
            Assert.That(GeometryMath.IsPointInPolygon(FenceWithBulge(bulge: true), new Vec2(-16, 0)), Is.True, "the scenario: and the implement's end is inside the fence");
            Assert.That(GeometryMath.IsPointInPolygon(turnLine, new Vec2(-12, -40)), Is.True, "the scenario: abreast of the tractor it is inside");
        });

        var plain = TurnFromTheSouth(Field(FenceWithBulge(bulge: false), hard), FenceNorth);
        var turn = TurnFromTheSouth(Field(FenceWithBulge(bulge: true), hard), FenceNorth);

        Assert.That(plain.Path, Is.Not.Null, "the scenario: a turn without the bulge");
        Assert.That(turn.Path, Is.Not.Null, "a turn");
        TestContext.Out.WriteLine($"without the bulge: {Length(plain.Path!):F0} m, top {plain.Path!.Max(p => p.Northing):F1}, lowest {plain.Path!.Min(p => p.Northing):F1}; "
            + $"with it: {Length(turn.Path!):F0} m, top {turn.Path!.Max(p => p.Northing):F1}, lowest {turn.Path!.Min(p => p.Northing):F1}");
        Assert.Multiple(() =>
        {
            Assert.That(turn.Path!.Min(p => p.Northing), Is.GreaterThan(30), "the turn stays at the end of the passes, not down the west side to the bulge");
            Assert.That(Length(turn.Path!), Is.EqualTo(Length(plain.Path!)).Within(2.0), "the same turn as without the bulge");
            Assert.That(turn.Path![^1].Easting, Is.EqualTo(-12).Within(0.5), "it lands on the next pass");
        });
    }

    /// <summary>
    /// AgOpenGPS (CYouTurn.FindABOutTurnPoint, as FindCurveOutTurnPoint) walks the turn line from
    /// the tractor's crossing in the turn's direction and takes the first segment the next pass
    /// crosses: the bulge, further on round the turn line, does not change its turn.
    /// </summary>
    [Test]
    public void AgOpenGPS_makes_the_same_turn_with_the_bulge_or_without()
    {
        List<Vec3> Theirs(bool bulge) => new AgOpenGpsYouTurnOracle(EveryTwoMetres(FenceWithBulge(bulge)))
        {
            youTurnRadius = 5.4, uturnDistanceFromBoundary = FromBoundary, uTurnStyle = 0,
            toolWidth = 8, toolOverlap = 0, toolOffset = 0, rowSkipsWidth = 2, // x = 4 to x = -12
        }.Build(new Vec3(4, -70, 0), new Vec3(0, -100, 0), new Vec3(0, 100, 0), 0, turnLeft: true);

        var plain = Theirs(bulge: false);
        var withBulge = Theirs(bulge: true);
        var ours = TurnFromTheSouth(Field(FenceWithBulge(bulge: true), hard: false), FenceNorth).Path;
        TestContext.Out.WriteLine($"AgOpenGPS without the bulge: {Length(plain):F0} m, with it: {Length(withBulge):F0} m, top {withBulge.Max(p => p.Northing):F1}; ours {ours!.Max(p => p.Northing):F1}");
        Assert.Multiple(() =>
        {
            Assert.That(withBulge, Has.Count.GreaterThan(10), "AgOpenGPS built a turn");
            Assert.That(Length(withBulge), Is.EqualTo(Length(plain)).Within(0.5), "the same turn as without the bulge");
            Assert.That(withBulge.Min(p => p.Northing), Is.GreaterThan(30), "at the end of the passes");
            Assert.That(ours.Max(p => p.Northing), Is.EqualTo(withBulge.Max(p => p.Northing)).Within(0.5), "ours reaches as far as theirs");
        });
    }

    /// <summary>The fence as recorded, a point every 2 m or less (the oracle's turn line is built from it).</summary>
    private static List<Vec2> EveryTwoMetres(List<Vec2> ring)
    {
        var points = new List<Vec2>();
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            int n = (int)Math.Ceiling(Math.Sqrt(GeometryMath.DistanceSquared(a, b)) / 2.0);
            for (int k = 0; k < n; k++)
                points.Add(new Vec2(a.Easting + (b.Easting - a.Easting) * k / n, a.Northing + (b.Northing - a.Northing) * k / n));
        }
        return points;
    }
}
