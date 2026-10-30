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
/// #411: a tap on the U-turn arrow swaps the planned turn to the other side, as AgOpenGPS's
/// SwapDirection does. The turn and the pass it lands on move together: in skip-worked
/// (snake) and alternative modes the swap used to flip only the arc, while the next pass
/// and the pass CompleteTurn jumps to stayed on the planned side. A side with no pass
/// inside the field keeps the planned turn.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class DirectionSwapTests
{
    // 6 m passes on an AB line north along x = 0: pass n's centre is at x = 6n + 3.
    // Fence at ±60, turn line 16 m inside it: passes -7..6 lie inside the cultivated area.
    private const double Width = 6;
    private static double PassX(int n) => n * Width + Width / 2;

    // ── Normal ───────────────────────────────────────────────────────────

    [Test]
    public void Normal_Swap_TurnsTheOtherWayOntoTheOtherNeighbour()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(0);

        bench.Tick(guidance, turn);
        Assert.That(turn.IsTurnLeft, Is.True, "the scenario: from pass 0 the planner turns left, onto pass -1");
        Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(-1)).Within(0.01));

        turn.NextUTurnDirectionLeftOverride = false;
        bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsTurnLeft, Is.False);
            Assert.That(turn.NextUTurnDirectionLeftOverride, Is.Null, "taken by this turn, never a later one");
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(1)).Within(0.01));
            Assert.That(turn.TurnPath, Is.Not.Null);
            Assert.That(turn.TurnPath![^1].Easting, Is.EqualTo(PassX(1)).Within(0.5), "the exit leg lands on pass 1");
        });
    }

    [Test]
    public void Normal_SwapTowardsTheFence_KeepsThePlannedTurn()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(6); // the last pass before the east fence

        bench.Tick(guidance, turn);
        Assert.That(turn.IsTurnLeft, Is.True, "the scenario: the only pass to turn onto is pass 5, to the west");

        turn.NextUTurnDirectionLeftOverride = false;
        var effects = bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsTurnLeft, Is.True, "pass 7 is outside the field");
            Assert.That(turn.NextUTurnDirectionLeftOverride, Is.Null);
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(5)).Within(0.01));
            Assert.That(effects.StatusMessage, Does.Contain("not changed"));
        });
    }

    // ── Skip worked passes (snake) ───────────────────────────────────────

    [Test]
    public void Snake_Swap_TurnsOntoTheNearestPassOnTheOtherSide()
    {
        var bench = new Bench(skipRows: 0, skipWorked: true);
        var (guidance, turn) = bench.OnPass(0);

        bench.Tick(guidance, turn);
        int planned = turn.ReturnPassTargetPath!.Value;
        int side = Math.Sign(planned);
        bool plannedLeft = turn.IsTurnLeft;

        turn.NextUTurnDirectionLeftOverride = !plannedLeft;
        bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsTurnLeft, Is.EqualTo(!plannedLeft));
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-side), "the neighbour on the other side");
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(-side)).Within(0.01),
                "the next pass is drawn on the side the turn now goes to");
            Assert.That(turn.NextTrackTurnOffset, Is.EqualTo(Width).Within(0.01));
            Assert.That(turn.TurnPath, Is.Not.Null);
        });
    }

    [Test]
    public void Snake_Swap_SkipsAWorkedPass_LikeGetNextNotWorkedTrack()
    {
        var bench = new Bench(skipRows: 0, skipWorked: true);
        var (guidance, turn) = bench.OnPass(0);

        bench.Tick(guidance, turn);
        int side = Math.Sign(turn.ReturnPassTargetPath!.Value);
        bench.Track.MarkPathWorked(-side);

        turn.NextUTurnDirectionLeftOverride = !turn.IsTurnLeft;
        bench.Tick(guidance, turn);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-2 * side), "the neighbour is worked: the next one over");
    }

    [Test]
    public void Snake_SwapToThePlannedSide_ChangesNothing()
    {
        var bench = new Bench(skipRows: 0, skipWorked: true);
        var (guidance, turn) = bench.OnPass(0);

        bench.Tick(guidance, turn);
        int planned = turn.ReturnPassTargetPath!.Value;

        turn.NextUTurnDirectionLeftOverride = turn.IsTurnLeft;
        bench.Tick(guidance, turn);

        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(planned));
        Assert.That(turn.NextUTurnDirectionLeftOverride, Is.Null);
    }

    [Test]
    public void Snake_AfterASwappedTurn_TheSnakeIsPlannedAgainFromTheNewPass()
    {
        var bench = new Bench(skipRows: 0, skipWorked: true);
        var (guidance, turn) = bench.OnPass(0);

        bench.Tick(guidance, turn);
        turn.NextUTurnDirectionLeftOverride = !turn.IsTurnLeft;
        bench.Tick(guidance, turn);
        int target = turn.ReturnPassTargetPath!.Value;

        turn.IsTriggered = turn.IsExecuting = true; // driven to the end
        bench.Complete(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(guidance.HowManyPathsAway, Is.EqualTo(target), "the turn ends on the pass it was swapped to");
            Assert.That(turn.SnakeSequence, Is.Null, "rebuilt from this pass by the next turn");
            Assert.That(turn.SnakeIndex, Is.EqualTo(-1));
        });
    }

    [Test]
    public void Snake_AfterAPlannedTurn_TheSnakeGoesOn()
    {
        var bench = new Bench(skipRows: 0, skipWorked: true);
        var (guidance, turn) = bench.OnPass(0);

        bench.Tick(guidance, turn);
        var sequence = turn.SnakeSequence;
        int planned = turn.ReturnPassTargetPath!.Value;

        turn.IsTriggered = turn.IsExecuting = true;
        bench.Complete(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(guidance.HowManyPathsAway, Is.EqualTo(planned));
            Assert.That(turn.SnakeSequence, Is.SameAs(sequence));
            Assert.That(turn.SnakeIndex, Is.EqualTo(1));
        });
    }

    // ── Alternative ──────────────────────────────────────────────────────

    [Test]
    public void Alternative_Swap_TurnsOntoThePatternPassOnTheOtherSide()
    {
        var bench = new Bench(skipRows: 1, alternate: true);
        var (guidance, turn) = bench.OnPass(0);

        bench.Tick(guidance, turn);
        int planned = turn.ReturnPassTargetPath!.Value;
        Assert.That(Math.Abs(planned), Is.EqualTo(2), "the scenario: skip 1 moves two passes");
        bool plannedLeft = turn.IsTurnLeft;

        turn.NextUTurnDirectionLeftOverride = !plannedLeft;
        bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsTurnLeft, Is.EqualTo(!plannedLeft));
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-planned));
            Assert.That(turn.AltSign, Is.EqualTo(Math.Sign(-planned)), "the pattern alternates on from the new side");
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(-planned)).Within(0.01));
        });
    }

    // ── Bench ────────────────────────────────────────────────────────────

    /// <summary>The real planner on a 120 m square field (6 m tool, 8 m radius, 16 m legs).</summary>
    private sealed class Bench
    {
        private readonly YouTurnStateMachine _sm;
        private readonly Boundary _boundary;
        private readonly List<Vec3> _turnLine;
        private readonly int _skipRows;
        private readonly bool _skipWorked, _alternate;
        private double _x;

        public Models.Track.Track Track { get; }

        public Bench(int skipRows, bool skipWorked = false, bool alternate = false)
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
            var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
            _sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

            var outer = new List<Vec2> { new(-60, -60), new(60, -60), new(60, 60), new(-60, 60) };
            _boundary = new Boundary
            {
                OuterBoundary = new BoundaryPolygon
                {
                    Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                },
            };
            _turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 16.0)!);
            Track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
            _skipRows = skipRows;
            _skipWorked = skipWorked;
            _alternate = alternate;
        }

        /// <summary>The tractor on pass <paramref name="pass"/>, heading north, 64 m before the turn line.</summary>
        public (GuidanceWorkingState, YouTurnWorkingState) OnPass(int pass)
        {
            _x = PassX(pass);
            return (new GuidanceWorkingState { HowManyPathsAway = pass }, new YouTurnWorkingState { IsEnabled = true });
        }

        private YouTurnStateMachine.TickContext Ctx() => new(
            new Position { Easting = _x, Northing = -20, Heading = 0, Speed = 2.78 },
            Track, _boundary, _turnLine,
            UTurnSkipRows: _skipRows, IsSkipWorkedMode: _skipWorked,
            HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0,
            IsAlternateSkipMode: _alternate);

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
