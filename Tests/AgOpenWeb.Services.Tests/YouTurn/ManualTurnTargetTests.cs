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
/// A manual turn, and a turn made "from here" when the tractor is already past the planned arc,
/// end on the line steered on the pass they complete onto (review of the U-turn code, 2026-11-03).
/// They took the skip + 1 passes and no tool offset, and used the planned pass only when it was
/// within half a pass of that: a skip-worked, snake or alternative plan landed on another pass
/// than the one guidance moved to, and a tool offset over a quarter of the width left the turn
/// twice the offset beside the line steered after it.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class ManualTurnTargetTests
{
    // 8 m passes on an AB line north along x = 0: pass n's centre is at x = 8n + 4.
    private const double Width = 8;
    private static double PassX(int n) => n * Width + Width / 2;

    [Test]
    public void SkipWorked_ManualTurn_EndsOnThePassGuidanceMovesTo()
    {
        var b = new Bench { SkipRows = 1, SkipWorked = true };
        var (g, t) = b.OnPass(-3);
        b.Track.MarkPathWorked(-1); // the skip would land here; the plan takes the next unworked pass

        var e = b.Sm.TriggerManual(turnLeft: false, isAutoSteerEngaged: true, b.Ctx(), g, t);
        Assert.That(t.IsExecuting, Is.True, e.StatusMessage);
        var end = t.TurnPath![^1];
        b.Sm.CompleteFromGuidance(b.Ctx(), g, t);

        Assert.That(end.Easting, Is.EqualTo(PassX(g.HowManyPathsAway)).Within(1.0),
            $"the path ends at x = {end.Easting:F1}, guidance went to pass {g.HowManyPathsAway}");
    }

    /// <summary>
    /// Past the arc start, 2 m beside the pass: the planned turn can't be joined, and a turn from
    /// here (about 24 m from the tractor at radius 8) would run 6 m past the fence of this 16 m
    /// headland. A turn the app starts by itself stays in the field: missed, and the operator is
    /// told. (Where it fits, the turn from here is made: YouTurnTriggerOnPathTests.)
    /// </summary>
    [Test]
    public void Snake_TurnFromHere_ThatWouldLeaveTheField_IsMissedWithAWarning()
    {
        var b = new Bench { SkipRows = 1, SkipWorked = true };
        var (g, t) = b.OnPass(1);
        b.Tick(g, t);
        Assert.That(t.TurnPath, Is.Not.Null, "the scenario: a snake turn planned");

        b.Y = t.ArcStart!.Value.Northing + 6;
        b.X = PassX(1) + 2.0;
        var e = b.Tick(g, t);
        Assert.Multiple(() =>
        {
            Assert.That(t.IsExecuting, Is.False, "no turn out of the field");
            Assert.That(t.TurnPath, Is.Null);
            Assert.That(e.StatusMessage, Does.Contain("missed"));
        });
    }

    // The tool 2.5 m right of the tractor: on pass 0 heading north the tractor steers x = 1.5;
    // back on pass 1 heading south, x = 14.5 (12 + 2.5), 13 m over, not 8.
    [Test]
    public void ToolOffset_ManualTurn_EndsOnTheLineSteeredAfterIt()
    {
        var b = new Bench(toolOffset: 2.5) { SkipRows = 0, SkipWorked = false };
        var (g, t) = b.OnPass(0);
        b.X = PassX(0) - 2.5;

        var e = b.Sm.TriggerManual(turnLeft: false, isAutoSteerEngaged: true, b.Ctx(), g, t);
        Assert.That(t.IsExecuting, Is.True, e.StatusMessage);

        Assert.Multiple(() =>
        {
            Assert.That(t.NextTrack!.Points[0].Easting, Is.EqualTo(14.5).Within(0.01), "the line steered on pass 1");
            Assert.That(t.TurnPath![^1].Easting, Is.EqualTo(14.5).Within(0.5));
        });
    }

    [TestCase(false, 14.5)] // right onto pass 1: 8 + 2 x 2.5 over
    [TestCase(true, -1.5)]  // left onto pass -1 (centre -4, tool on the tractor's right = west): 8 - 2 x 2.5 over
    public void ToolOffset_WithoutAPlannedPass_TheTurnIsTwiceTheOffsetWiderOrNarrower(bool left, double endX)
    {
        var b = new Bench(toolOffset: 2.5);
        var (g, _) = b.OnPass(0);
        b.X = PassX(0) - 2.5; // x = 1.5

        var path = b.Creation.CreateManualArcPath(b.Ctx().CurrentPosition, 0, left, b.Boundary, g, uTurnSkipRows: 0);

        Assert.That(path, Is.Not.Empty);
        Assert.That(path[^1].Easting, Is.EqualTo(endX).Within(0.5));
    }

    /// <summary>The real planner on a 76 x 120 m field (8 m tool, 8 m radius, 16 m headland).</summary>
    private sealed class Bench
    {
        public readonly YouTurnStateMachine Sm;
        public readonly YouTurnCreationService Creation;
        public readonly Boundary Boundary;
        private readonly List<Vec3> _turnLine;
        public readonly Models.Track.Track Track;
        public double X, Y;
        public int SkipRows;
        public bool SkipWorked;

        public Bench(double toolOffset = 0)
        {
            ConfigurationStore.SetInstance(new ConfigurationStore());
            var c = ConfigurationStore.Instance;
            c.Vehicle.Wheelbase = 2.5;
            c.Vehicle.MaxSteerAngle = 35;
            c.Tool.Overlap = 0;
            c.Tool.Offset = toolOffset;
            c.NumSections = 1;
            c.Tool.SetSectionWidth(0, (int)(Width * 100));
            c.Guidance.UTurnRadius = 8.0;
            c.Guidance.UTurnExtension = 16.0;
            c.Guidance.UTurnDistanceFromBoundary = 2.0;
            c.Guidance.UTurnStyle = 0;

            var offset = new PolygonOffsetService();
            Creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
            var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
            Sm = new YouTurnStateMachine(Creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

            var outer = new List<Vec2> { new(-38, -60), new(38, -60), new(38, 60), new(-38, 60) };
            Boundary = new Boundary
            {
                OuterBoundary = new BoundaryPolygon
                {
                    Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                },
            };
            _turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 16.0)!);
            Track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));
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
            Track, Boundary, _turnLine,
            UTurnSkipRows: SkipRows, IsSkipWorkedMode: SkipWorked,
            HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0);

        public YouTurnEffects Tick(GuidanceWorkingState g, YouTurnWorkingState t) => Sm.Tick(Ctx(), g, t);
    }
}
