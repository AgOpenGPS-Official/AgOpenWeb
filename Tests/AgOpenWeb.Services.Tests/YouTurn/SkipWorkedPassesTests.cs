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

using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// Skip mode "Ignore worked tracks" (the snake) never turns onto a pass that is already
/// worked, and plans from the pass the tractor is on. It used to take the next pass of an
/// order fixed at the first turn: a worked pass, or after the operator moved to another
/// pass midway, the pass the tractor was already on. A pass is worked when sections were
/// on while the tractor was on it, or when the job's coverage covers at least half of it
/// (worked before the field was reopened).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SkipWorkedPassesTests
{
    // The field of the bug report: six 8 m passes, -3 (west) to 2 (east), on an AB line
    // north along x = 0. Pass n's centre is at x = 8n + 4. Fence at x = ±38, turn line 16 m
    // inside it, so passes -4 (x = -28) and 3 (x = 28) are outside the cultivated area.
    private const double Width = 8;
    private static double PassX(int n) => n * Width + Width / 2;

    // The snake over passes -3..2 is -3, -1, 1, 2, 0, -2: two passes per turn, then back.

    [Test]
    public void TheTwoPassesBesideAreWorked_TheTurnGoesToAnUnworkedPass()
    {
        // The report: on the west pass heading south, the two passes on its left (east)
        // already worked, skip 1. The turn went onto pass -1, worked.
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-3, headingSouth: true);
        bench.Worked(-3, -2, -1);

        bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(1), "-1 is worked: the next pass of the order");
            Assert.That(turn.IsTurnLeft, Is.True, "east, heading south");
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(1)).Within(0.01));
            Assert.That(turn.TurnPath, Is.Not.Null);
        });
    }

    [Test]
    public void PassesCoveredOnTheMap_CountAsWorked()
    {
        // Reopened job: the passes are green on the map, but nothing was marked this session.
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-3, headingSouth: true);
        bench.Covered = (e, n) => e > PassX(-3) - Width / 2 && e < PassX(-1) + Width / 2;

        bench.Tick(guidance, turn);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(1));
    }

    [Test]
    public void APassCoveredForLessThanHalfItsLength_IsNotWorked()
    {
        // A U-turn painted across the end of pass -1; the rest of it is still to do.
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-3, headingSouth: true);
        bench.Covered = (e, n) => n < -30 && Math.Abs(e - PassX(-1)) < Width / 2;

        bench.Tick(guidance, turn);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-1));
    }

    [Test]
    public void MovingToAnotherPassMidway_PlansFromThatPass()
    {
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-2, headingSouth: true);
        bench.Worked(-2, -1);

        bench.Tick(guidance, turn);
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-3), "the scenario: from -2 the order goes on to -3");

        // The operator moves onto -3 and works it: the planned turn is dropped, as a snap does.
        bench.MoveTo(guidance, -3);
        bench.Worked(-3);
        YouTurnStateMachine.DiscardPlannedTurn(turn);
        bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.SnakeSequence![0], Is.EqualTo(-3), "planned again from the pass the tractor is on");
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(1), "not -3, the pass it is on, nor -1, worked");
        });
    }

    [Test]
    public void EveryPassWorked_NoTurn()
    {
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(0);
        bench.Worked(-3, -2, -1, 0, 1, 2);

        var effects = bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null);
            Assert.That(turn.ReturnPassTargetPath, Is.Null);
            Assert.That(effects.StatusMessage, Does.Contain("Field complete"));
        });
    }

    [Test]
    public void AfterATurnOverWorkedPasses_TheOrderGoesOnFromThePassItLandedOn()
    {
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-3, headingSouth: true);
        bench.Worked(-3, -1);

        bench.Tick(guidance, turn);
        var sequence = turn.SnakeSequence;
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(1));

        turn.IsTriggered = turn.IsExecuting = true; // driven to the end
        bench.Complete(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(guidance.HowManyPathsAway, Is.EqualTo(1));
            Assert.That(turn.SnakeSequence, Is.SameAs(sequence), "the plan still holds");
            Assert.That(turn.SnakeSequence![turn.SnakeIndex], Is.EqualTo(1));
        });

        bench.MoveTo(guidance, 1, headingSouth: false);
        bench.Worked(1);
        bench.Tick(guidance, turn);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(2));
    }

    [Test]
    public void AtTheEndOfAFinishedField_PassesAreNotTestedOnEveryCycle()
    {
        // A finished field plans no turn, so the planner asks again on every cycle, from the
        // cultivated area and then from the headland. The map test samples a pass across the
        // whole field: it is made once per plan, not once per cycle.
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-3, headingSouth: true);
        int lookups = 0;
        bench.Covered = (e, n) => { lookups++; return true; };

        bench.Tick(guidance, turn);
        int firstCycle = lookups;
        for (int i = 0; i < 20; i++) bench.Tick(guidance, turn);
        bench.DriveTo(-50); // past the turn line, in the headland
        for (int i = 0; i < 20; i++) bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(firstCycle, Is.GreaterThan(0), "the first cycle tests the passes");
            Assert.That(lookups, Is.EqualTo(firstCycle), "no cycle after it tests them again");
            Assert.That(turn.TurnPath, Is.Null);
        });
    }

    [Test]
    public void APassOutsideTheOrder_IsPlannedOnce()
    {
        // The tractor on a pass whose middle is outside the cultivated area (an odd-shaped
        // field): the plan starts from it and goes on to the first pass of the order. It used
        // to leave the plan off the tractor's pass, so the plan was made again on every cycle.
        var bench = new Bench();
        var guidance = new GuidanceWorkingState { HowManyPathsAway = -4 };
        var turn = new YouTurnWorkingState();

        bench.Plan(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(YouTurnPathingService.IsSnakeOnPass(turn, -4), Is.True);
            Assert.That(bench.Pathing.GetNextSnakePath(turn, _ => false), Is.EqualTo(-3), "the first pass of the order");
        });
    }

    [Test]
    public void OnACurve_APassCoveredOnTheMap_IsWorked()
    {
        var bench = new Bench();
        var curve = Models.Track.Track.FromCurve("Curve",
            Enumerable.Range(-100, 201).Select(y => new Vec3(0, y, 0)).ToList());
        Func<double, double, bool> covered = (e, n) => Math.Abs(e - PassX(-1)) < Width / 2;

        Assert.Multiple(() =>
        {
            Assert.That(bench.IsPassWorked(curve, -1, covered), Is.True);
            Assert.That(bench.IsPassWorked(curve, 0, covered), Is.False);
        });
    }

    // ── Bench ────────────────────────────────────────────────────────────

    /// <summary>The real planner on the report's field (8 m tool, 8 m radius, 16 m legs), skip 1.</summary>
    private sealed class Bench
    {
        private readonly YouTurnStateMachine _sm;
        private readonly Boundary _boundary;
        private readonly List<Vec3> _turnLine;
        private readonly Models.Track.Track _track;
        private double _x, _y, _heading;

        /// <summary>The job's coverage map (easting, northing); null = nothing covered.</summary>
        public Func<double, double, bool>? Covered { get; set; }

        public YouTurnPathingService Pathing { get; }

        public Bench()
        {
            ConfigurationStore.SetInstance(new ConfigurationStore());
            var c = ConfigurationStore.Instance;
            c.Vehicle.Wheelbase = 2.5;
            c.Vehicle.MaxSteerAngle = 35;
            c.Tool.Overlap = 0;
            c.NumSections = 1;
            c.Tool.SetSectionWidth(0, (int)(Width * 100));
            c.Guidance.UTurnRadius = 8.0;
            c.Guidance.UTurnExtension = 16.0;
            c.Guidance.UTurnDistanceFromBoundary = 2.0;
            c.Guidance.UTurnStyle = 0;

            var offset = new PolygonOffsetService();
            var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
            Pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
            _sm = new YouTurnStateMachine(creation, Pathing, NullLogger<YouTurnStateMachine>.Instance, c);

            var outer = new List<Vec2> { new(-38, -60), new(38, -60), new(38, 60), new(-38, 60) };
            _boundary = new Boundary
            {
                OuterBoundary = new BoundaryPolygon
                {
                    Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                },
            };
            _turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 16.0)!);
            _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
        }

        /// <summary>The tractor on <paramref name="pass"/>, 64 m before the turn line ahead of it.</summary>
        public (GuidanceWorkingState, YouTurnWorkingState) OnPass(int pass, bool headingSouth = false)
        {
            var guidance = new GuidanceWorkingState();
            MoveTo(guidance, pass, headingSouth);
            return (guidance, new YouTurnWorkingState { IsEnabled = true });
        }

        /// <summary>The tractor moves onto <paramref name="pass"/>; guidance follows it.</summary>
        public void MoveTo(GuidanceWorkingState guidance, int pass, bool headingSouth = true)
        {
            _x = PassX(pass);
            _y = headingSouth ? 20 : -20;
            _heading = headingSouth ? 180 : 0;
            guidance.HowManyPathsAway = pass;
        }

        /// <summary>The tractor drives on along its pass to <paramref name="northing"/>.</summary>
        public void DriveTo(double northing) => _y = northing;

        /// <summary>Plan the snake from the pass <paramref name="guidance"/> is on.</summary>
        public void Plan(GuidanceWorkingState guidance, YouTurnWorkingState turn) =>
            Pathing.BuildSnakeSequence(_track, 0, guidance, turn, _boundary, _turnLine);

        public bool IsPassWorked(Models.Track.Track track, int pass, Func<double, double, bool> covered) =>
            Pathing.IsPassWorked(track, pass, 0, _boundary, _turnLine, covered);

        /// <summary>Sections were on while the tractor was on these passes.</summary>
        public void Worked(params int[] passes)
        {
            foreach (int p in passes) _track.MarkPathWorked(p);
        }

        private YouTurnStateMachine.TickContext Ctx() => new(
            new Position { Easting = _x, Northing = _y, Heading = _heading, Speed = 2.78 },
            _track, _boundary, _turnLine,
            UTurnSkipRows: 1, IsSkipWorkedMode: true,
            HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0,
            IsPointCovered: Covered);

        public YouTurnEffects Tick(GuidanceWorkingState guidance, YouTurnWorkingState turn)
        {
            var effects = _sm.Tick(Ctx(), guidance, turn);
            Assert.That(turn.IsExecuting, Is.False, "the tractor is still far short of the turn");
            return effects;
        }

        public void Complete(GuidanceWorkingState guidance, YouTurnWorkingState turn) =>
            _sm.CompleteFromGuidance(Ctx(), guidance, turn);
    }
}
