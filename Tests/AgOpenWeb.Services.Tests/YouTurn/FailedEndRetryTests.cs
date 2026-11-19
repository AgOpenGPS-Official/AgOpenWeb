// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
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
/// The normal and alternative modes: an end where no turn was made is tried again every 1.5 s,
/// or at once when something that can change the answer does, as the snake order's ends
/// (<see cref="SnakeOpenPassTests"/>, #852). Built again on every GPS cycle, a turn that
/// could not be made filled the 2000 lines of a bug report's log in 13 s (the tractor parked
/// with AutoSteer on, 95 builds, each moving the turn back 0.5 m at a time up to 30 m).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class FailedEndRetryTests
{
    private const double ToolWidth = 4.0;

    // Pass k is x = 4k + 2.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private YouTurnCreationService _creation = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private PolygonOffsetService _offset = null!;
    private List<Vec3> _turnLine = null!;

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
        // A 32 m loop onto a pass 8 m over, the hard fence 2 m past the turn line: no turn fits,
        // even moved back towards the tractor.
        c.Guidance.UTurnRadius = 16.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        _offset = new PolygonOffsetService();
        _creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, _offset, c);
        _sm = new YouTurnStateMachine(
            _creation,
            new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c),
            NullLogger<YouTurnStateMachine>.Instance, c);

        var outer = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        _boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        };
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        _turnLine = _offset.CalculatePointHeadings(new List<Vec2> { new(-48, -48), new(48, -48), new(48, 48), new(-48, 48) });
    }

    private YouTurnStateMachine.TickContext Ctx(double e, double n, double headingDeg, bool alternate) => new(
        new Position { Easting = e, Northing = n, Heading = headingDeg, Speed = 2.78 },
        _track, _boundary, _turnLine,
        UTurnSkipRows: 1, IsSkipWorkedMode: false,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
        IsAlternateSkipMode: alternate);

    // On pass 0 heading south, 30 m from the turn line.
    private static (GuidanceWorkingState Guidance, YouTurnWorkingState Turn) OnPass0() =>
        (new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = false },
            new YouTurnWorkingState { IsEnabled = true });

    [TestCase(false, TestName = "Normal mode: an end where no turn builds is tried again every 1.5 s, not on every cycle")]
    [TestCase(true, TestName = "Alternative mode: an end where no turn builds is tried again every 1.5 s, not on every cycle")]
    public void Tried_again_every_second_and_a_half(bool alternate)
    {
        var (guidance, turn) = OnPass0();

        var first = _sm.Tick(Ctx(PassX(0), -18, 180, alternate), guidance, turn);
        int builds = _creation.TurnPathBuilds;
        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, $"the scenario: no turn fits (\"{first.StatusMessage}\")");
            Assert.That(builds, Is.GreaterThan(0), "the scenario: a turn was built and did not fit");
            Assert.That(first.TurnCreationFailedSound, Is.True, "the operator is told once");
        });

        // Fourteen more cycles, parked (the bug report) or creeping on: nothing built.
        for (int i = 1; i <= 14; i++)
            _sm.Tick(Ctx(PassX(0), -18 - 0.01 * i, 180, alternate), guidance, turn);
        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(builds), "not built again on every cycle");

        // The fifteenth, 1.5 s on: tried again, silently.
        var retry = _sm.Tick(Ctx(PassX(0), -18.15, 180, alternate), guidance, turn);
        Assert.Multiple(() =>
        {
            Assert.That(_creation.TurnPathBuilds, Is.EqualTo(2 * builds), "tried again after 1.5 s");
            Assert.That(retry.TurnCreationFailedSound, Is.False, "silently");
            Assert.That(retry.TellOperator, Is.False, "the operator was told once");
        });
    }

    [Test]
    public void Normal_mode_an_end_where_no_turn_built_is_tried_again_at_once_when_something_changes()
    {
        var (guidance, turn) = OnPass0();
        _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        int builds = _creation.TurnPathBuilds;

        void TriedAgain(string what, YouTurnStateMachine.TickContext ctx)
        {
            _sm.Tick(ctx, guidance, turn);
            Assert.That(_creation.TurnPathBuilds, Is.GreaterThan(builds), what);
            builds = _creation.TurnPathBuilds;
        }

        // The U-turn arrow: the operator picks a side.
        turn.NextUTurnDirectionLeftOverride = true;
        TriedAgain("the U-turn arrow", Ctx(PassX(0), -18, 180, false));

        // Another pass (a lateral move): its end is another end.
        guidance.HowManyPathsAway = 1;
        TriedAgain("another pass", Ctx(PassX(1), -18, 180, false));

        // The alternative mode chosen.
        TriedAgain("the alternative mode", Ctx(PassX(1), -18, 180, true));

        // The fence made soft, still in the alternative mode: the turn may now swing over it.
        _boundary.OuterBoundary!.IsHard = false;
        TriedAgain("the fence made soft", Ctx(PassX(1), -18, 180, true));
    }

    [Test]
    public void Normal_mode_a_heading_flickering_off_the_line_does_not_build_the_turn_again()
    {
        // Parked with AutoSteer on, a single antenna's heading swings past the 20 degrees the turn is
        // looked for within, and back: the end is not tried again until the 1.5 s are up.
        var (guidance, turn) = OnPass0();
        _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        int builds = _creation.TurnPathBuilds;
        Assert.That(builds, Is.GreaterThan(0), "the scenario: a turn was built and did not fit");

        for (int i = 1; i <= 6; i++)
        {
            _sm.Tick(Ctx(PassX(0), -18, 180 + 30, false), guidance, turn); // off the line
            _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        }
        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(builds), "not built again at each flicker");

        // Off the line for 1.5 s (turned away, and back): tried again at once.
        for (int i = 0; i < 15; i++) _sm.Tick(Ctx(PassX(0), -18, 180 + 30, false), guidance, turn);
        _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        Assert.That(_creation.TurnPathBuilds, Is.GreaterThan(builds), "tried again after 1.5 s off the line");
    }

    [Test]
    public void Normal_mode_a_turn_radius_lowered_to_make_it_fit_is_built_at_once()
    {
        var (guidance, turn) = OnPass0();
        _sm.Tick(Ctx(PassX(0), -18, 180, false), guidance, turn);
        Assert.That(turn.TurnPath, Is.Null, "the scenario: no turn at 16 m");

        ConfigurationStore.Instance.Guidance.UTurnRadius = 4.0;
        _sm.Tick(Ctx(PassX(0), -18.01, 180, false), guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null, "the radius changed: tried again at once, and it fits");
    }
}
