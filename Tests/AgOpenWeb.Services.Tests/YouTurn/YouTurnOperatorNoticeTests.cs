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

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The operator is told why a U-turn was not made (bug report "manual U-turn does nothing",
/// 2026-11-11). The state machine's status message is not sent to the web client, so a manual
/// U-turn that was refused did nothing at all on the screen, and an automatic turn that could
/// not be made or was missed only sounded. <see cref="YouTurnEffects.TellOperator"/> marks the
/// messages the operator must see; the pipeline hands them on as
/// <c>GpsCycleResult.YouTurnNotice</c> and the host shows them as a toast.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class YouTurnOperatorNoticeTests
{
    [Test]
    public void AManualPressWithAutoSteerOff_IsToldToTheOperator()
    {
        var b = new Bench();
        var (g, t) = b.OnPass(0);

        var e = b.Sm.TriggerManual(turnLeft: false, isAutoSteerEngaged: false, b.Ctx(), g, t);

        Assert.Multiple(() =>
        {
            Assert.That(t.IsExecuting, Is.False);
            Assert.That(e.TellOperator, Is.True);
            Assert.That(e.StatusMessage, Does.Contain("autosteer"));
        });
    }

    [Test]
    public void AManualTurnThatStarts_IsNotANotice_ASecondPressDuringItIs()
    {
        var b = new Bench();
        var (g, t) = b.OnPass(0);

        var started = b.Sm.TriggerManual(turnLeft: false, isAutoSteerEngaged: true, b.Ctx(), g, t);
        Assert.That(t.IsExecuting, Is.True, started.StatusMessage);
        Assert.That(started.TellOperator, Is.False, "a turn that starts is seen on the map");

        var again = b.Sm.TriggerManual(turnLeft: false, isAutoSteerEngaged: true, b.Ctx(), g, t);
        Assert.Multiple(() =>
        {
            Assert.That(again.TellOperator, Is.True);
            Assert.That(again.StatusMessage, Does.Contain("already in progress"));
            Assert.That(t.IsExecuting, Is.True, "the turn being driven is left alone");
        });
    }

    [Test]
    public void AManualTurnRefusedAtAHardBoundary_SaysThereIsNoRoom()
    {
        var b = new Bench(hard: true);
        var (g, t) = b.OnPass(0);
        b.Y = 50; // 10 m from the north fence: the 8 m tool would go through it

        var e = b.Sm.TriggerManual(turnLeft: false, isAutoSteerEngaged: true, b.Ctx(), g, t);

        Assert.Multiple(() =>
        {
            Assert.That(t.IsExecuting, Is.False);
            Assert.That(e.TellOperator, Is.True);
            Assert.That(e.StatusMessage, Does.Contain("Not enough room"));
        });
    }

    /// <summary>
    /// Past the arc start and 2 m beside the pass, with a turn from here that would leave the
    /// field: the planned turn is missed. Said once, with the sound, not on every cycle after.
    /// </summary>
    [Test]
    public void AMissedAutomaticTurn_IsToldOnce_WithItsSound()
    {
        var b = new Bench { SkipRows = 1, SkipWorked = true };
        var (g, t) = b.OnPass(1);
        b.Tick(g, t);
        Assert.That(t.TurnPath, Is.Not.Null, "the scenario: a turn planned");

        b.Y = t.ArcStart!.Value.Northing + 6;
        b.X = Bench.PassX(1) + 2.0;
        var missed = b.Tick(g, t);
        var next = b.Tick(g, t);

        Assert.Multiple(() =>
        {
            Assert.That(missed.StatusMessage, Does.Contain("missed"));
            Assert.That(missed.TurnCreationFailedSound, Is.True);
            Assert.That(missed.TellOperator, Is.True);
            Assert.That(next.TellOperator, Is.False, "once, as the sound");
        });
    }

    [Test]
    public void ATurnPlannedAndStarted_IsNotANotice()
    {
        var b = new Bench();
        var (g, t) = b.OnPass(0);

        var planned = b.Tick(g, t);
        Assert.That(t.TurnPath, Is.Not.Null, "the scenario: a turn planned");
        Assert.That(planned.TellOperator, Is.False);
    }

    /// <summary>The real planner on a 76 x 120 m field (8 m tool, 8 m radius, 16 m headland).</summary>
    private sealed class Bench
    {
        private const double Width = 8;
        public static double PassX(int n) => n * Width + Width / 2;

        public readonly YouTurnStateMachine Sm;
        public readonly Boundary Boundary;
        private readonly List<Vec3> _turnLine;
        private readonly Models.Track.Track _track;
        public double X, Y;
        public int SkipRows;
        public bool SkipWorked;

        public Bench(bool hard = false)
        {
            ConfigurationStore.SetInstance(new ConfigurationStore());
            var c = ConfigurationStore.Instance;
            c.Vehicle.Wheelbase = 2.5;
            c.Vehicle.MaxSteerAngle = 35;
            c.Tool.Overlap = 0;
            c.Tool.Offset = 0;
            c.NumSections = 1;
            c.Tool.SetSectionWidth(0, (int)(Width * 100));
            c.Guidance.UTurnRadius = 8.0;
            c.Guidance.UTurnExtension = 16.0;
            c.Guidance.UTurnDistanceFromBoundary = 2.0;
            c.Guidance.UTurnStyle = 0;

            var offset = new PolygonOffsetService();
            var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
            var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
            Sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

            var outer = new List<Vec2> { new(-38, -60), new(38, -60), new(38, 60), new(-38, 60) };
            Boundary = new Boundary
            {
                OuterBoundary = new BoundaryPolygon
                {
                    Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                    IsHard = hard,
                },
            };
            _turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 16.0)!);
            _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        }

        /// <summary>The tractor on pass <paramref name="pass"/>, heading north, 20 m south of the middle.</summary>
        public (GuidanceWorkingState, YouTurnWorkingState) OnPass(int pass)
        {
            X = PassX(pass);
            Y = -20;
            return (new GuidanceWorkingState { HowManyPathsAway = pass, IsHeadingSameWay = true },
                new YouTurnWorkingState { IsEnabled = true });
        }

        public YouTurnStateMachine.TickContext Ctx() => new(
            new Position { Easting = X, Northing = Y, Heading = 0, Speed = 2.78 },
            _track, Boundary, _turnLine,
            UTurnSkipRows: SkipRows, IsSkipWorkedMode: SkipWorked,
            HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0);

        public YouTurnEffects Tick(GuidanceWorkingState g, YouTurnWorkingState t) => Sm.Tick(Ctx(), g, t);
    }
}
