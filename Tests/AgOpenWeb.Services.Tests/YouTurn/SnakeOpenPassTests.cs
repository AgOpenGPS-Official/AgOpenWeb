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
/// The snake order (skip worked passes) goes on to its next pass still to do. When that pass has
/// no room at this end, the turn goes onto the nearest pass still to do that has, instead of
/// saying the end of the field with passes left beside the tractor; and the order is planned
/// again when the headland changes, as it was planned for the passes of the old one. On a
/// tester's field (simulator) the order went from 31R to 7L across the field (no room at that
/// end: "End of field reached") with 28R open, and after the headland was built again the turn went right onto a
/// pass by the hard fence the old order had next, where it would not build.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeOpenPassTests
{
    private const double ToolWidth = 4.0;

    // Pass k is x = 4k + 2.
    private static double PassX(int k) => 4 * k + 2;

    private YouTurnStateMachine _sm = null!;
    private YouTurnCreationService _creation = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private PolygonOffsetService _offset = null!;

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
            },
        };
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
    }

    private List<Vec3> Ring(params (double E, double N)[] points) =>
        _offset.CalculatePointHeadings(points.Select(p => new Vec2(p.E, p.N)).ToList());

    // The hourglass of LandingLengthTests: an 80 x 80 m cultivated square with a notch in its east
    // side from (40,-30) in to (6,0) and out to (40,30). At each end pass 6 runs 22.4 m, 7 18.8 m,
    // 8 15.3 m, 9 11.8 m; the passes west of the notch run 25.9 m (pass 5) and more.
    private List<Vec3> Hourglass() => Ring((-40, -40), (40, -40), (40, -30), (6, 0), (40, 30), (40, 40), (-40, 40));

    private YouTurnStateMachine.TickContext Ctx(List<Vec3> cultivated, double e, double n, double headingDeg) => new(
        new Position { Easting = e, Northing = n, Heading = headingDeg, Speed = 2.78 },
        _track, _boundary, cultivated,
        UTurnSkipRows: 1, IsSkipWorkedMode: true,
        HeadlandCalculatedWidth: 10.0, HeadlandDistance: 10.0,
        IsAlternateSkipMode: false);

    [Test]
    public void The_order_s_next_pass_with_no_room_here_turns_onto_the_nearest_pass_still_to_do()
    {
        // On pass 6 heading south, 15 m from the south edge. The order (passes -10..9) goes on east
        // to pass 8: 15.3 m at this end, less than 20 m and less than pass 6's own. East, pass 9
        // has 11.8 m and pass 7 18.8 m. West, pass 4 runs on through the field.
        var cultivated = Hourglass();
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        var effects = _sm.Tick(Ctx(cultivated, PassX(6), -25, 180), guidance, turn);

        Assert.That(turn.SnakeSequence!.Take(2), Is.EqualTo(new[] { 6, 8 }), "the scenario: the order goes on to pass 8");
        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Not.Null, $"a turn, not \"{effects.StatusMessage}\"");
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(4), "the nearest pass still to do with room, two over");
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(4)).Within(0.01));
            Assert.That(effects.TurnCreationFailedSound, Is.False);
        });
    }

    [Test]
    public void No_pass_still_to_do_with_room_here_is_still_no_room_at_this_end()
    {
        // As above with every pass west of the notch worked: only the short passes east are left.
        var cultivated = Hourglass();
        for (int k = -10; k <= 5; k++) _track.MarkPathWorked(k);
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };

        var effects = _sm.Tick(Ctx(cultivated, PassX(6), -25, 180), guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null);
            Assert.That(effects.StatusMessage, Is.EqualTo("End of field reached - no room on the next pass at this end"));
            Assert.That(effects.TurnCreationFailedSound, Is.True);
        });
    }

    [Test]
    public void The_order_is_planned_again_when_the_headland_changes()
    {
        // The square -40..40 (passes -10..9): from pass 6 the order goes east to 8.
        var square = Ring((-40, -40), (40, -40), (40, 40), (-40, 40));
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        _sm.Tick(Ctx(square, PassX(6), -25, 180), guidance, turn);
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(8), "the scenario: the order for the old headland");

        // The headland built again 4 m wider on the west side: passes -9..9, one fewer, and the
        // order for them goes west from pass 6. Pass 8 is still in the field.
        var narrower = Ring((-36, -40), (40, -40), (40, 40), (-36, 40));
        YouTurnStateMachine.DiscardPlannedTurn(turn);
        _sm.Tick(Ctx(narrower, PassX(6), -25, 180), guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.SnakeSequence, Does.Not.Contain(-10), "planned for the new headland");
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(4), "the new order's next pass");
        });
    }

    [Test]
    public void Landed_in_the_headland_facing_the_field_is_not_the_end_of_the_pass()
    {
        // A turn whose last metres are not driven ends in the headland: the tractor is on its new
        // pass (8, heading north) half a metre short of the headland line. That is the start of
        // the pass, not its end: no end of the field, no sound, no turn back here. Taken as the end,
        // every pass had no room "at this end" and the end of the field was said as the turn finished.
        var square = Ring((-40, -40), (40, -40), (40, 40), (-40, 40));
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        _sm.Tick(Ctx(square, PassX(6), -25, 180), guidance, turn);
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(8), "the scenario: a turn onto pass 8");
        turn.IsTriggered = turn.IsExecuting = true;
        _sm.CompleteFromGuidance(Ctx(square, PassX(8), -40.5, 0), guidance, turn);
        Assert.That(guidance.HowManyPathsAway, Is.EqualTo(8));

        var effects = _sm.Tick(Ctx(square, PassX(8), -40.5, 0), guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(effects.StatusMessage, Is.Null);
            Assert.That(effects.TurnCreationFailedSound, Is.False);
            Assert.That(turn.TurnPath, Is.Null, "no turn at the end it came in by");
        });

        // In the field, the turn at the far end is planned.
        _sm.Tick(Ctx(square, PassX(8), -35, 0), guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null);
        Assert.That(turn.ArcStart!.Value.Northing, Is.GreaterThan(30), "at the north end");
    }

    [Test]
    public void The_same_headland_sent_again_keeps_the_order()
    {
        // The pipeline sends the headland again as a new list on every command: the same line is
        // the same field, and the order and where it has got to stay.
        var square = Ring((-40, -40), (40, -40), (40, 40), (-40, 40));
        var guidance = new GuidanceWorkingState { HowManyPathsAway = 6, IsHeadingSameWay = false };
        var turn = new YouTurnWorkingState { IsEnabled = true };
        _sm.Tick(Ctx(square, PassX(6), -25, 180), guidance, turn);
        var plan = turn.SnakeSequence;

        YouTurnStateMachine.DiscardPlannedTurn(turn);
        _sm.Tick(Ctx(square.ToList(), PassX(6), -25, 180), guidance, turn);

        Assert.That(turn.SnakeSequence, Is.SameAs(plan));
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(8));
    }

    // A hard fence 2 m past the headland line and a 32 m loop: from pass 0 heading south, 30 m from
    // the headland line, no turn onto pass 2 (the order's), 4 or -2 clears the fence, even moved
    // back towards the tractor.
    private (GuidanceWorkingState Guidance, YouTurnWorkingState Turn, List<Vec3> Cultivated) FailingEnd()
    {
        ConfigurationStore.Instance.Guidance.UTurnRadius = 16.0;
        _boundary.OuterBoundary!.IsHard = true;
        return (new GuidanceWorkingState { HowManyPathsAway = 0, IsHeadingSameWay = false },
            new YouTurnWorkingState { IsEnabled = true },
            Ring((-48, -48), (48, -48), (48, 48), (-48, 48)));
    }

    [Test]
    public void An_end_where_no_turn_builds_is_not_tried_again_on_every_cycle()
    {
        // Tick looks for a turn on every GPS cycle while the end is ahead (10 m to 2 km). Each look
        // built the order's turn and one onto the nearest open pass on each side, each moving the
        // turn back from the fence 0.5 m at a time up to 30 m: three builds a cycle, for nothing.
        var (guidance, turn, cultivated) = FailingEnd();

        var first = _sm.Tick(Ctx(cultivated, PassX(0), -18, 180), guidance, turn);
        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "the scenario: no turn fits");
            Assert.That(_creation.TurnPathBuilds, Is.EqualTo(3), "the order's pass, then one on each side");
            Assert.That(first.TurnCreationFailedSound, Is.True);
        });

        // Thirty more cycles towards the end, 0.3 m apart.
        for (int i = 1; i <= 30; i++)
            _sm.Tick(Ctx(cultivated, PassX(0), -18 - 0.3 * i, 180), guidance, turn);

        Assert.That(_creation.TurnPathBuilds, Is.EqualTo(3), "not built again on every cycle");
    }

    [Test]
    public void An_end_where_no_turn_built_is_tried_again_when_something_changes()
    {
        var (guidance, turn, cultivated) = FailingEnd();
        _sm.Tick(Ctx(cultivated, PassX(0), -18, 180), guidance, turn);
        int builds = _creation.TurnPathBuilds;

        void TriedAgain(string what, YouTurnStateMachine.TickContext ctx)
        {
            _sm.Tick(ctx, guidance, turn);
            Assert.That(_creation.TurnPathBuilds, Is.GreaterThan(builds), what);
            builds = _creation.TurnPathBuilds;
            _sm.Tick(ctx, guidance, turn);
            if (turn.TurnPath == null)
                Assert.That(_creation.TurnPathBuilds, Is.EqualTo(builds), what + ": then remembered");
        }

        // The U-turn arrow: the operator picks a side.
        turn.NextUTurnDirectionLeftOverride = true;
        TriedAgain("the U-turn arrow", Ctx(cultivated, PassX(0), -19, 180));

        // Another pass (a lateral move): its end is another end.
        guidance.HowManyPathsAway = 1;
        TriedAgain("another pass", Ctx(cultivated, PassX(1), -19, 180));

        // The other way along the pass: the north end.
        TriedAgain("the other direction", Ctx(cultivated, PassX(1), 18, 0));

        // Closer than 10 m to the end, Tick stops looking for a turn. Looking again (a pass that
        // crosses a headland strip has another end past it) tries again.
        _sm.Tick(Ctx(cultivated, PassX(1), 39, 0), guidance, turn);
        TriedAgain("after Tick stopped looking for a turn", Ctx(cultivated, PassX(1), 18, 0));

        // A smaller turn radius: the operator makes it fit.
        ConfigurationStore.Instance.Guidance.UTurnRadius = 4.0;
        _sm.Tick(Ctx(cultivated, PassX(1), 19, 0), guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null, "the turn now fits and is planned");
    }
}
