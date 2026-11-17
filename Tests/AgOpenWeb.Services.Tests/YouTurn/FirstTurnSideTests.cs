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
/// The first turn on a track goes the way the work is. The pass along the edge the operator
/// started beside can have a sliver in the work area (15 m of pass -15 on Olival, field job J20;
/// J11, J17), and any sample inside counted it as a pass: both sides tied, and the tie-break sent
/// the first turn at the edge, where the end of the field would have been said with the whole
/// field undone. A side counts only with a pass worth driving there: the landing length or more.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class FirstTurnSideTests
{
    private YouTurnStateMachine _sm = null!;
    private YouTurnPathingService _pathing = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 800);
        c.Guidance.UTurnRadius = 4.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;
        var offset = new PolygonOffsetService();
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        _sm = new YouTurnStateMachine(
            new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c),
            _pathing, NullLogger<YouTurnStateMachine>.Instance, c);
    }

    // The fence 0..100 east, -150..150 north; the turn line 2 m in. The AB line at x = 20 heading
    // north is the edge of pass 0 (x = 24, 8 m wide); pass -1 is at x = 16, pass 1 at x = 32. The
    // work area 18..90 east, with a 4 m bump out to 14 from -17 to -5 north: pass -1 has 12 m of it
    // inside, pass 1 all of it.
    private static readonly List<Vec3> TurnLine = Ring((2, -148), (98, -148), (98, 148), (2, 148));
    private static readonly List<Vec3> Work = Ring((18, -140), (18, -17), (14, -17), (14, -5), (18, -5), (18, 140), (90, 140), (90, -140));
    private static readonly Boundary Fence = new()
    {
        OuterBoundary = new BoundaryPolygon { Points = new List<BoundaryPoint> { new(0, -150, 0), new(100, -150, 0), new(100, 150, 0), new(0, 150, 0) } },
    };
    private static readonly AgOpenWeb.Models.Track.Track Track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(20, -200, 0), new Vec3(20, 200, 0));

    private static List<Vec3> Ring(params (double E, double N)[] p) => p.Select(q => new Vec3(q.E, q.N, 0)).ToList();

    [Test]
    public void A_pass_with_a_sliver_in_the_work_area_does_not_draw_the_first_turn()
    {
        Assert.That(_pathing.InsideLength(Track, 0, -1, Fence, Work), Is.EqualTo(12).Within(5.1), "the sliver: 12 m of pass -1");
        Assert.That(_pathing.InsideLength(Track, 0, 1, Fence, Work, stopAt: 50), Is.GreaterThanOrEqualTo(50), "pass 1 is a whole pass");

        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = 24, Northing = -100, Heading = 0, Speed = 2.78 },
            Track, Fence, TurnLine,
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
            WorkHeadlandLine: Work);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        _sm.Tick(ctx, guidance, turn);

        Assert.That(turn.TurnPath, Is.Not.Null, "a turn is planned for the far end");
        Assert.That(turn.TurnPath![^1].Easting, Is.EqualTo(32).Within(1.0), "onto pass 1, the field's side, not the sliver at the edge");
    }

    [Test]
    public void Alternate_skip_starts_its_pattern_the_way_the_work_is()
    {
        // One row skipped: the pattern steps two passes. Pass -2 (x = 8) is in the headland band,
        // inside the turn line and landable, with nothing of it in the work area (J11: pass -12 in
        // the 42 m band); pass 2 (x = 40) is in the field.
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = 24, Northing = -100, Heading = 0, Speed = 2.78 },
            Track, Fence, TurnLine,
            UTurnSkipRows: 1, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
            IsAlternateSkipMode: true,
            WorkHeadlandLine: Work);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = true };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        _sm.Tick(ctx, guidance, turn);

        Assert.That(turn.TurnPath, Is.Not.Null, "a turn is planned for the far end");
        Assert.That(turn.TurnPath![^1].Easting, Is.EqualTo(40).Within(1.0), "onto pass 2, the field's side");
        Assert.That(turn.AltSign, Is.EqualTo(1));
    }
}
