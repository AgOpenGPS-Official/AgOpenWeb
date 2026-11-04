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

    // ── Normal: the side holds while the turn is planned again (#577) ─────

    [TestCase(1)]
    [TestCase(3)]
    public void Normal_Swap_HoldsWhenTheSkipChanges(int newSkip)
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(-1);

        bench.Tick(guidance, turn);
        Assert.That(turn.IsTurnLeft, Is.True, "the scenario: the planner turns left");
        turn.NextUTurnDirectionLeftOverride = false;
        bench.Tick(guidance, turn);
        Assert.That(turn.IsTurnLeft, Is.False, "swapped to the right");

        // The skip buttons: the pipeline drops the planned turn and the next tick plans again.
        bench.SkipRows = newSkip;
        YouTurnStateMachine.DiscardPlannedTurn(turn);
        bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsTurnLeft, Is.False, "still to the right, as the operator set it");
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(-1 + newSkip + 1)).Within(0.01));
        });
    }

    // #585: the skip (and the skip mode) changed while the turn was being driven. The turn was
    // built to land on one pass; completing it must put guidance on that pass, not on the pass
    // the new skip would have chosen from where the turn started.
    [TestCase(false)]
    [TestCase(true)]
    public void Normal_SkipChangedDuringTheTurn_LandsOnThePassTheTurnWasBuiltFor(bool alternateToo)
    {
        var bench = new Bench(skipRows: 3);
        var (guidance, turn) = bench.OnPass(-1);
        bench.Tick(guidance, turn);
        double plannedX = turn.NextTrack!.Points[0].Easting;
        int planned = (int)Math.Round((plannedX - Width / 2) / Width); // PassX inverted
        Assert.That(Math.Abs(planned - -1), Is.EqualTo(4), "the scenario: skip 3 moves four passes");

        turn.IsTriggered = turn.IsExecuting = true; // driving the turn
        bench.SkipRows = 0;
        bench.Alternate = alternateToo;
        bench.Complete(guidance, turn);

        Assert.That(guidance.HowManyPathsAway, Is.EqualTo(planned),
            "on the pass the turn reached, not one picked with the new skip");
    }

    [Test]
    public void Normal_SkipChangedDuringTheTurn_AppliesFromTheNextTurn()
    {
        var bench = new Bench(skipRows: 3);
        var (guidance, turn) = bench.OnPass(-1);
        bench.Tick(guidance, turn);
        turn.IsTriggered = turn.IsExecuting = true;
        bench.SkipRows = 0;
        bench.Complete(guidance, turn);
        int landed = guidance.HowManyPathsAway;

        int next = bench.TurnAndComplete(guidance, turn);
        Assert.That(Math.Abs(next - landed), Is.EqualTo(1), "the next turn moves by the new skip");
    }

    // #588: stopped in the turn and reversed back out of it. Guidance reports the path left;
    // the turn didn't happen, so guidance stays on the pass and the turn is planned again.
    [Test]
    public void ReversedOutOfTheTurn_StaysOnThePass_AndPlansTheTurnAgain()
    {
        var bench = new Bench(skipRows: 3);
        var (guidance, turn) = bench.OnPass(-1);
        bench.Tick(guidance, turn);
        turn.IsTriggered = turn.IsExecuting = true;

        guidance.IsReverse = true;
        var effects = bench.Complete(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(guidance.HowManyPathsAway, Is.EqualTo(-1), "still on the pass it backed out to");
            Assert.That(turn.IsExecuting, Is.False);
            Assert.That(turn.TurnPath, Is.Null);
            Assert.That(effects.TurnCompleted, Is.False, "nothing was completed");
        });

        guidance.IsReverse = false;
        bench.Tick(guidance, turn);
        Assert.That(turn.TurnPath, Is.Not.Null, "driving forward again, the turn is planned again");
        int planned = (int)Math.Round((turn.NextTrack!.Points[0].Easting - Width / 2) / Width);
        Assert.That(Math.Abs(planned - -1), Is.EqualTo(4), "to the same pass as before");
    }

    [Test]
    public void KTurn_InReverse_StillCompletes()
    {
        var bench = new Bench(skipRows: 0);
        ConfigurationStore.Instance.Guidance.UTurnStyle = 1;
        var (guidance, turn) = bench.OnPass(-1);
        bench.Tick(guidance, turn);
        turn.IsTriggered = turn.IsExecuting = true;

        guidance.IsReverse = true;
        bench.Complete(guidance, turn);
        Assert.That(guidance.HowManyPathsAway, Is.Not.EqualTo(-1), "a K-turn's reversing is part of the turn");
    }

    [Test]
    public void Normal_Swap_IsForTheOneTurn()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(-3);

        Assert.That(bench.TurnAndComplete(guidance, turn, swap: true), Is.EqualTo(-2), "the scenario: swapped east");
        Assert.That(turn.HeldDirectionLeft, Is.Null, "the side was for that turn");
        YouTurnStateMachine.DiscardPlannedTurn(turn);
        Assert.That(turn.NextUTurnDirectionLeftOverride, Is.Null, "a later plan is not swapped");
    }

    // ── Normal: no pass to turn onto (#570, Graeme's later dump) ─────────

    [Test]
    public void Normal_NoPassToTurnOnto_SoundsOnce_UntilUTurnIsSwitchedOff()
    {
        // 20 rows skipped: the passes 21 away either side are outside the field.
        var bench = new Bench(skipRows: 20);
        var (guidance, turn) = bench.OnPass(0);

        var first = bench.Tick(guidance, turn);
        var second = bench.Tick(guidance, turn);
        bench.SwitchedOff();
        var afterOff = bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null);
            Assert.That(first.StatusMessage, Does.Contain("End of field"));
            Assert.That(first.TurnCreationFailedSound, Is.True, "the operator must take over: say so");
            Assert.That(second.TurnCreationFailedSound, Is.False, "once, not every cycle");
            Assert.That(afterOff.TurnCreationFailedSound, Is.True, "U-turn off and on: again");
        });
    }

    // ── Normal: the work goes on across the field (#523) ──────────────────

    [Test]
    public void Normal_FromTheFieldEdge_KeepsSteppingAcrossTheField()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(-7); // the westmost pass: the only way on is east

        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(-6));
        // Both neighbours of -6 are inside the field; -7 is the pass just worked.
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(-5), "on east, not back onto pass -7");
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(-4));
    }

    [Test]
    public void Normal_AfterASwappedTurn_CarriesOnToTheNewSide()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(-3);

        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(-4), "the scenario: the first turn goes west");
        Assert.That(bench.TurnAndComplete(guidance, turn, swap: true), Is.EqualTo(-3), "swapped: east");
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(-2), "on east, not back onto pass -4");
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

    // ── The lines shown with a turn (#591) ──────────────────────────────

    // An alternative-skip turn: the next pass shown (cyan) is where the turn ends and the line
    // steered after it, nudge included. It used to leave the nudge out and sat beside the turn.
    [TestCase(false)]
    [TestCase(true)]
    public void PlannedPass_NextPassShown_HasTheNudge(bool alternate)
    {
        var bench = new Bench(skipRows: 1, skipWorked: !alternate, alternate: alternate);
        var (guidance, turn) = bench.OnPass(0);
        guidance.NudgeOffset = 2.0;

        bench.Tick(guidance, turn);
        int target = turn.ReturnPassTargetPath!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(turn.NextTrack!.Points[0].Easting, Is.EqualTo(PassX(target) + 2.0).Within(0.01));
            Assert.That(turn.TurnPath![^1].Easting, Is.EqualTo(PassX(target) + 2.0).Within(0.5), "the turn ends there too");
        });
    }

    // Halfway round the turn the tractor heads the other way. Which way the pass is driven stays
    // as it was until the turn completes: it places the old pass's line with a tool offset, which
    // jumped by twice the offset halfway round the arc.
    [Test]
    public void DrivingTheTurn_TheDirectionIsHeldUntilItCompletes()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(0);
        bench.Tick(guidance, turn);
        Assert.That(guidance.IsHeadingSameWay, Is.True, "the scenario: heading north along the line");

        turn.IsTriggered = turn.IsExecuting = true;
        bench.Heading = 180; // round the arc, heading south
        bench.TickAny(guidance, turn);

        Assert.That(guidance.IsHeadingSameWay, Is.True);
    }

    [Test]
    public void AfterTheTurn_TheNewDirectionIsTaken()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(0);
        bench.Heading = 180;
        bench.TickAny(guidance, turn);
        Assert.That(guidance.IsHeadingSameWay, Is.False);
    }

    // ── A snake order made for another field ────────────────────────────

    // The order was planned before the headland or boundary changed: its next pass (9, x = 57)
    // is now outside the turn line (passes -7..6 are inside). The turn must not go there: the
    // snake is planned again for the field as it is.
    [Test]
    public void Snake_NextPassOfTheOrderOutsideTheField_PlansTheOrderAgain()
    {
        var bench = new Bench(skipRows: 0, skipWorked: true);
        var (guidance, turn) = bench.OnPass(0);
        turn.SnakeSequence = new List<int> { 0, 9, -1 };
        turn.SnakeIndex = 0;

        bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.ReturnPassTargetPath, Is.Not.EqualTo(9));
            Assert.That(turn.ReturnPassTargetPath, Is.InRange(-7, 6), "a pass inside the field");
            Assert.That(turn.SnakeSequence, Does.Not.Contain(9));
        });
    }

    // ── The end of the field ────────────────────────────────────────────

    // Pass 6 is the last inside. Turning onto it from 5, the next pass that way (7) is outside:
    // the only pass inside is 5, just worked. The field is done; the turn used to go back onto 5.
    [Test]
    public void Normal_LastPass_EndsTheFieldInsteadOfTurningBackOntoTheWorkedPass()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(5);
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(6), "the scenario: 5 -> 6, the last pass");

        var effects = bench.Tick(guidance, turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "no turn back onto pass 5");
            Assert.That(effects.StatusMessage, Is.EqualTo("End of field reached"));
        });
    }

    [Test]
    public void Normal_LastPass_TheArrowStillTurnsBack()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(5);
        bench.TurnAndComplete(guidance, turn);

        bench.Tick(guidance, turn);
        turn.NextUTurnDirectionLeftOverride = true; // left, heading north: toward pass 5
        bench.Tick(guidance, turn);

        Assert.That(turn.TurnPath, Is.Not.Null);
    }

    // Skip 1 from pass 4: onto 6, the last. Turning back goes two passes, onto 4, just worked.
    [Test]
    public void SkipRows_LastPass_EndsTheFieldToo()
    {
        var bench = new Bench(skipRows: 1);
        var (guidance, turn) = bench.OnPass(4);
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(6));

        var effects = bench.Tick(guidance, turn);

        Assert.That(turn.TurnPath, Is.Null, "no turn back onto pass 4");
        Assert.That(effects.StatusMessage, Is.EqualTo("End of field reached"));
    }

    // After a turn the operator moved to the last pass (a snap, or drove there): it was not
    // reached by a turn, so the inside side is the way.
    [Test]
    public void Normal_MovedToTheLastPass_TurnsInward()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(4);
        Assert.That(bench.TurnAndComplete(guidance, turn), Is.EqualTo(5));
        var (_, fresh) = bench.OnPass(6);
        guidance.HowManyPathsAway = 6;

        bench.Tick(guidance, fresh);

        Assert.That(fresh.PlannedNextPath, Is.EqualTo(5));
    }

    // The tractor starts on the last pass: before any turn, the inside side is the way.
    [Test]
    public void Normal_StartingOnTheLastPass_TurnsInward()
    {
        var bench = new Bench(skipRows: 0);
        var (guidance, turn) = bench.OnPass(6);

        bench.Tick(guidance, turn);

        Assert.That(turn.PlannedNextPath, Is.EqualTo(5));
    }

    // ── Bench ────────────────────────────────────────────────────────────

    /// <summary>The real planner on a 120 m square field (6 m tool, 8 m radius, 16 m legs).</summary>
    private sealed class Bench
    {
        private readonly YouTurnStateMachine _sm;
        private readonly Boundary _boundary;
        private readonly List<Vec3> _turnLine;
        private readonly bool _skipWorked;

        /// <summary>Alternative skip mode; the operator can switch it at any time.</summary>
        public bool Alternate { get; set; }

        /// <summary>The skip; changing it is what the pipeline does on the skip buttons.</summary>
        public int SkipRows { get; set; }
        private double _x;

        /// <summary>The tractor's heading (degrees); north, along the line, unless a test turns it.</summary>
        public double Heading { get; set; }

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
            SkipRows = skipRows;
            _skipWorked = skipWorked;
            Alternate = alternate;
        }

        /// <summary>The tractor on pass <paramref name="pass"/>, heading north, 64 m before the turn line.</summary>
        public (GuidanceWorkingState, YouTurnWorkingState) OnPass(int pass)
        {
            _x = PassX(pass);
            return (new GuidanceWorkingState { HowManyPathsAway = pass }, new YouTurnWorkingState { IsEnabled = true });
        }

        private YouTurnStateMachine.TickContext Ctx() => new(
            new Position { Easting = _x, Northing = -20, Heading = Heading, Speed = 2.78 },
            Track, _boundary, _turnLine,
            UTurnSkipRows: SkipRows, IsSkipWorkedMode: _skipWorked,
            HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0,
            IsAlternateSkipMode: Alternate);

        public YouTurnEffects Tick(GuidanceWorkingState guidance, YouTurnWorkingState turn)
        {
            var effects = _sm.Tick(Ctx(), guidance, turn);
            Assert.That(turn.IsExecuting, Is.False, "the tractor is still far short of the turn");
            return effects;
        }

        /// <summary>One cycle, whatever the turn is doing.</summary>
        public YouTurnEffects TickAny(GuidanceWorkingState guidance, YouTurnWorkingState turn) => _sm.Tick(Ctx(), guidance, turn);

        public void SwitchedOff() => _sm.OnYouTurnSwitchedOff();

        public YouTurnEffects Complete(GuidanceWorkingState guidance, YouTurnWorkingState turn) =>
            _sm.CompleteFromGuidance(Ctx(), guidance, turn);

        /// <summary>Plan a turn (swapped to the other side if asked), drive it, and stand on the
        /// pass it lands on, 64 m before the turn line again.</summary>
        public int TurnAndComplete(GuidanceWorkingState guidance, YouTurnWorkingState turn, bool swap = false)
        {
            Tick(guidance, turn);
            if (swap)
            {
                turn.NextUTurnDirectionLeftOverride = !turn.IsTurnLeft;
                Tick(guidance, turn);
            }
            turn.IsTriggered = turn.IsExecuting = true;
            Complete(guidance, turn);
            _x = PassX(guidance.HowManyPathsAway);
            return guidance.HowManyPathsAway;
        }
    }
}
