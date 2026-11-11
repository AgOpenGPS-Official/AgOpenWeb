// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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
/// "End of field reached" tells the operator the job is done. When the turns can't go on from
/// this end but the next pass on the way the turns step lies in the field further on, the
/// operator has to take over and drive there: the message must say there is no room at this end,
/// not that the field is done. Field job J3 (Baetas) stopped in a narrowing lobe with 40 % of the
/// field left: the next pass had 14.9 m at that end, the pass just worked had room, so the turn
/// would have stepped back, and the plain "End of field reached" was said.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class EndOfFieldMessageTests
{
    private const double ToolWidth = 4.0;

    // The hourglass of LandingLengthTests: an 80 x 80 m cultivated square with a notch in its east
    // side from (40,-30) in to (6,0) and out to (40,30). Pass k is x = 4k + 2. Each pass has the
    // same run at its north and its south end: pass 5 25.9 m, 6 22.4 m, 7 18.8 m, 8 15.3 m.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private Boundary _boundary = null!;
    private List<Vec3> _cultivated = null!;
    private Models.Track.Track _track = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(ToolWidth * 100));
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        var offset = new PolygonOffsetService();
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
            NullLogger<YouTurnStateMachine>.Instance, c);

        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        var ring = new List<Vec2>
        {
            new(-40, -40), new(40, -40), new(40, -30), new(6, 0), new(40, 30), new(40, 40), new(-40, 40),
        };
        _cultivated = offset.CalculatePointHeadings(ring);
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
    }

    private YouTurnStateMachine.TickContext Ctx(double e, double n, double headingDeg) => new(
        new Position { Easting = e, Northing = n, Heading = headingDeg, Speed = 2.78 },
        _track, _boundary, _cultivated,
        UTurnSkipRows: 0, IsSkipWorkedMode: false,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
        IsAlternateSkipMode: false);

    [Test]
    public void No_room_at_this_end_but_the_next_pass_goes_on_in_the_field_is_not_the_end_of_the_field()
    {
        // Pass 5 heading south, 15 m before the south end: the turn steps east onto pass 6.
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 5, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var ctx = Ctx(PassX(5), -25, 180);
        _sm.Tick(ctx, guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null, "the scenario: a turn is planned from pass 5");
        Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(6)).Within(0.01), "the scenario: onto pass 6, east");
        turn.IsTriggered = turn.IsExecuting = true;
        _sm.CompleteFromGuidance(ctx, guidance, turn);
        Assert.That(guidance.HowManyPathsAway, Is.EqualTo(6));

        // Pass 6 heading north, 13 m before the north end. Pass 7 has 18.8 m there, less than
        // 20 m and less than pass 6's 22.4 m: no landing. Pass 5 has room, but was just worked.
        // Pass 7 runs on through the south half of the field: the operator takes over and
        // drives there.
        guidance.IsHeadingSameWay = true;
        var effects = _sm.Tick(Ctx(PassX(6), 27, 0), guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "no turn back onto the pass just worked");
            Assert.That(effects.StatusMessage, Is.EqualTo("End of field reached - no room on the next pass at this end"));
        });
    }

    [Test]
    public void Stepped_back_at_the_real_edge_of_the_field_is_the_end_of_the_field()
    {
        // The west edge: pass -10 (x = -38) is the last inside the cultivated area (x >= -40);
        // pass -11 (x = -42) is in the headland along its whole length. Stepping west from
        // pass -9 onto -10, the next turn could only go back east: the field is done.
        var guidance = new GuidanceWorkingState { HowManyPathsAway = -9, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        var ctx = Ctx(PassX(-9), -25, 180);
        _sm.Tick(ctx, guidance, turn);
        Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(-10)).Within(0.01), "the scenario: onto pass -10, west");
        turn.IsTriggered = turn.IsExecuting = true;
        _sm.CompleteFromGuidance(ctx, guidance, turn);

        guidance.IsHeadingSameWay = true;
        var effects = _sm.Tick(Ctx(PassX(-10), 25, 0), guidance, turn);

        Assert.That(effects.StatusMessage, Is.EqualTo("End of field reached"));
    }
}
