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
using AgOpenWeb.Services.Track;
using AgOpenWeb.Services.YouTurn;

using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The skip button's third mode, ignore worked tracks (the snake order), skips the rows the
/// operator chose, as the other two modes do. Its order was the fixed snake, one pass in two,
/// whatever the row count: on a tester's field (25 Nov 2026, 3 rows chosen) every tap of the row
/// button planned the same turn again, onto the pass two over, and the curve on the map never
/// changed.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton.
public class SnakeSkipRowsTests
{
    // ── The order ────────────────────────────────────────────────────────

    [Test]
    public void OneRow_IsTheSnakeOrderAsBefore()
    {
        // Every range and every pass the tractor can be on, inside the range or just outside it.
        foreach (int min in new[] { -7, 0, 3 })
            for (int n = 1; n <= 15; n++)
            {
                int max = min + n - 1;
                for (int current = min - 2; current <= max + 2; current++)
                    Assert.That(SwathOrderingService.SkipAndFillFrom(min, max, current, 2), Is.EqualTo(SnakeAsBefore(min, max, current)),
                        $"passes {min}..{max}, from {current}");
            }
    }

    [Test]
    public void ThreeRows_FromTheEdge_EveryFourthPassOutAndBack()
    {
        var order = SwathOrderingService.SkipAndFillFrom(0, 11, 0, 4);

        Assert.That(order, Is.EqualTo(new[] { 0, 4, 8, 9, 5, 1, 2, 6, 10, 11, 7, 3 }));
    }

    [Test]
    public void EveryPassIsInTheOrderOnce()
    {
        for (int step = 2; step <= 10; step++)
            for (int n = 1; n <= 30; n++)
                for (int current = 0; current < n; current++)
                {
                    var order = SwathOrderingService.SkipAndFillFrom(-5, -5 + n - 1, -5 + current, step);
                    Assert.Multiple(() =>
                    {
                        Assert.That(order[0], Is.EqualTo(-5 + current), "starts on the tractor's pass");
                        Assert.That(order.OrderBy(p => p), Is.EqualTo(Enumerable.Range(-5, n)), $"{n} passes, step {step}, from {-5 + current}");
                    });
                }
    }

    [Test]
    public void AStepLongerThanTheField_IsTheSameOrderAsOneAsLongAsIt()
    {
        // Every pass a lane of its own either way; int.MaxValue must not walk int.MaxValue lanes.
        for (int n = 1; n <= 12; n++)
            for (int current = -1; current <= n; current++)
                Assert.That(SwathOrderingService.SkipAndFillFrom(0, n - 1, current, int.MaxValue),
                    Is.EqualTo(SwathOrderingService.SkipAndFillFrom(0, n - 1, current, n)), $"{n} passes, from {current}");
    }

    [Test]
    public void FromAnEdge_NoMoveIsLongerThanTheStep()
    {
        // The rows skipped from one pass to the next, a lane at a time; between lanes, at the
        // ends of the field, fewer. No move along the headland past the skip.
        for (int step = 2; step <= 10; step++)
            for (int n = 1; n <= 40; n++)
                foreach (int start in new[] { 0, n - 1 })
                {
                    var order = SwathOrderingService.SkipAndFillFrom(0, n - 1, start, step);
                    for (int i = 1; i < order.Count; i++)
                        Assert.That(Math.Abs(order[i] - order[i - 1]), Is.LessThanOrEqualTo(step),
                            $"{n} passes, step {step}, from {start}: [{string.Join(", ", order)}]");
                }
    }

    [Test]
    public void TheMovesAlongALaneAreTheStep()
    {
        var order = SwathOrderingService.SkipAndFillFrom(0, 39, 0, 5);
        int lanePasses = order.Zip(order.Skip(1)).Count(m => Math.Abs(m.Second - m.First) == 5);

        // 40 passes in 5 lanes of 8: 7 moves in each lane, 4 moves from lane to lane.
        Assert.That(lanePasses, Is.EqualTo(35), "[" + string.Join(", ", order) + "]");
    }

    // ── The U-turn ───────────────────────────────────────────────────────

    [Test]
    public void ThreeRows_TheTurnGoesFourPassesOver()
    {
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-13, headingSouth: true);

        bench.Tick(guidance, turn, skipRows: 3);

        Assert.Multiple(() =>
        {
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-9), "3 rows skipped: -12, -11 and -10");
            Assert.That(turn.TurnPath, Is.Not.Null);
            Assert.That(turn.SnakeSequence!.Take(4), Is.EqualTo(new[] { -13, -9, -5, -1 }));
        });
    }

    [Test]
    public void TheRowButtonTapped_TheTurnIsPlannedForTheNewCount()
    {
        // The report: the snake on, the turn planned two passes over (1 row); the operator taps the
        // row button. The pipeline drops the order and the planned turn (GpsPipelineService's
        // resetSnake); planned again, the turn went to the same pass, as the order skipped one row
        // whatever the count.
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(-13, headingSouth: true);
        bench.Tick(guidance, turn, skipRows: 1);
        Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-11), "1 row: two passes over");
        var firstTurn = turn.TurnPath!.ToList();

        turn.SnakeSequence = null;
        turn.SnakeIndex = -1;
        YouTurnStateMachine.DiscardPlannedTurn(turn);
        bench.Tick(guidance, turn, skipRows: 3);

        Assert.Multiple(() =>
        {
            Assert.That(turn.ReturnPassTargetPath, Is.EqualTo(-9), "3 rows: four passes over");
            Assert.That(turn.TurnPath, Is.Not.Null);
            Assert.That(turn.TurnPath![^1].Easting - firstTurn[^1].Easting, Is.EqualTo(2 * Width).Within(0.5),
                "the curve on the map ends two passes further on");
        });
    }

    [Test]
    public void InTheMiddleOfTheField_TheTurnGoesTheSkipOver()
    {
        var bench = new Bench();
        var (guidance, turn) = bench.OnPass(0, headingSouth: true);

        bench.Tick(guidance, turn, skipRows: 3);

        Assert.That(Math.Abs(turn.ReturnPassTargetPath!.Value), Is.EqualTo(4));
    }

    // The order before: the snake over the range, turned to start on the tractor's pass, or that
    // pass put in front when it is outside the range.
    private static List<int> SnakeAsBefore(int min, int max, int current)
    {
        var full = SwathOrderingService.GeneratePathSequence(min, max, SwathPattern.Snake);
        int at = full.IndexOf(current);
        return at > 0 ? full.Skip(at).Concat(full.Take(at)).ToList()
            : at < 0 ? full.Prepend(current).ToList()
            : full;
    }

    // ── Bench ────────────────────────────────────────────────────────────

    // 8 m passes on an AB line north along x = 0: pass n's centre is at x = 8n + 4. Fence at
    // x = ±120 and y = ±60, turn line 16 m inside it: passes -13 (x = -100) to 12 (x = 100).
    private const double Width = 8;
    private static double PassX(int n) => n * Width + Width / 2;

    /// <summary>The real planner (8 m tool, 8 m radius, 16 m legs) in the snake order.</summary>
    private sealed class Bench
    {
        private readonly YouTurnStateMachine _sm;
        private readonly Boundary _boundary;
        private readonly List<Vec3> _turnLine;
        private readonly Models.Track.Track _track;
        private double _x, _y, _heading;

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
            var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
            _sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

            var outer = new List<Vec2> { new(-120, -60), new(120, -60), new(120, 60), new(-120, 60) };
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
        public (GuidanceWorkingState, YouTurnWorkingState) OnPass(int pass, bool headingSouth)
        {
            _x = PassX(pass);
            _y = headingSouth ? 20 : -20;
            _heading = headingSouth ? 180 : 0;
            return (new GuidanceWorkingState { HowManyPathsAway = pass }, new YouTurnWorkingState { IsEnabled = true });
        }

        public void Tick(GuidanceWorkingState guidance, YouTurnWorkingState turn, int skipRows)
        {
            var ctx = new YouTurnStateMachine.TickContext(
                new Position { Easting = _x, Northing = _y, Heading = _heading, Speed = 2.78 },
                _track, _boundary, _turnLine,
                UTurnSkipRows: skipRows, IsSkipWorkedMode: true,
                HeadlandCalculatedWidth: 16.0, HeadlandDistance: 16.0);
            _sm.Tick(ctx, guidance, turn);
            Assert.That(turn.IsExecuting, Is.False, "the tractor is still far short of the turn");
        }
    }
}
