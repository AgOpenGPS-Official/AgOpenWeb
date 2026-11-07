// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// The automatic U-turn at an obstacle whose choice is a U-turn (<see cref="ObstacleAction.Turn"/>),
/// as AgOpenGPS turns at the turn line round every inner boundary with Drive thru = No: a pass that
/// runs into the obstacle turns before it, and no part of the turn comes nearer the obstacle than
/// the U-turn distance from boundary. A drive-through obstacle gets no U-turn and the turn is
/// at the field's edge.
/// </summary>
[TestFixture]
public class ObstacleUTurnTests
{
    private const double ObstacleSouth = 40, ObstacleNorth = 60, Distance = 1.0;

    private YouTurnCreationService _creation = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600); // 6 m
        c.Guidance.UTurnDistanceFromBoundary = Distance;
        c.Guidance.UTurnRadius = 6.0;
        _creation = new YouTurnCreationService(
            NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), ConfigurationStore.Instance);
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static BoundaryPolygon Rect(double e0, double n0, double e1, double n1)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e1, n0), (e1, n1), (e0, n1) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    // A 200 x 300 m field, a 20 m obstacle across pass 0 (the line E = 0) 40 m north of the tractor.
    private static Boundary Field(ObstacleAction at, double e0 = -10, double e1 = 10, bool hard = false)
    {
        var b = new Boundary { OuterBoundary = Rect(-100, -150, 100, 150) };
        var obstacle = Rect(e0, ObstacleSouth, e1, ObstacleNorth);
        obstacle.AtObstacle = at;
        obstacle.IsHard = hard;
        b.InnerBoundaries.Add(obstacle);
        return b;
    }

    private static List<Vec3> Headland() => new()
    {
        new(-80, -130, 0), new(80, -130, 0), new(80, 130, 0), new(-80, 130, 0)
    };

    private YouTurnCreationService.TurnPathResult Turn(Boundary boundary, double northing = 0, bool north = true)
    {
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));
        // On pass 0, which lies half a pass beside the AB line.
        double pass = Models.Track.PassGeometry.EdgeOffset(track, ConfigurationStore.Instance.ActualToolWidth);
        var pos = new Position { Easting = pass, Northing = northing, Heading = north ? 0 : 180 };
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = north, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true };
        return _creation.CreateTurnPath(
            pos, track, headingRadians: north ? 0 : System.Math.PI, abHeading: 0,
            boundary, Headland(), guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 20, headlandDistance: 20);
    }

    [Test]
    public void APassRunningIntoAUTurnObstacle_TurnsBeforeIt()
    {
        var result = Turn(Field(ObstacleAction.Turn));

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10));
        Assert.That(result.AtObstacle, Is.True);
        Assert.That(result.UsedFallback, Is.False);
        double far = result.Path!.Max(p => p.Northing);
        Assert.That(far, Is.LessThanOrEqualTo(ObstacleSouth - Distance + 0.2), "no nearer the obstacle than the U-turn distance");
        Assert.That(far, Is.GreaterThan(ObstacleSouth - 10), "at the obstacle, not somewhere short of it");
    }

    [Test]
    public void FromTheOtherSide_TheTurnIsBeyondTheObstacle()
    {
        var result = Turn(Field(ObstacleAction.Turn), northing: 100, north: false);

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10));
        Assert.That(result.AtObstacle, Is.True);
        Assert.That(result.Path!.Min(p => p.Northing), Is.GreaterThanOrEqualTo(ObstacleNorth + Distance - 0.2));
    }

    [Test]
    public void ADriveThroughObstacle_IsPassed_AndTheTurnIsAtTheFieldsEdge()
    {
        var result = Turn(Field(ObstacleAction.DriveThrough));

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10));
        Assert.That(result.AtObstacle, Is.False);
        Assert.That(result.Path!.Max(p => p.Northing), Is.GreaterThan(130), "beyond the headland line, by the outer boundary");
    }

    [Test]
    public void AnObstacleBesideThePass_IsNotTurnedAt()
    {
        var result = Turn(Field(ObstacleAction.Turn, e0: 30, e1: 50));

        Assert.That(result.AtObstacle, Is.False);
        Assert.That(result.Path!.Max(p => p.Northing), Is.GreaterThan(130));
    }

    [Test]
    public void WhenNoTurnFitsBeforeTheObstacle_ThereIsNoTurn_NotOneAtTheFieldsEdge()
    {
        // 1.5 m from the obstacle's turn line: too late to turn there. The fallback, laid out from
        // the outer boundary, would drive the tractor through the obstacle.
        var result = Turn(Field(ObstacleAction.Turn), northing: ObstacleSouth - Distance - 1.5);

        Assert.That(result.Path, Is.Null);
        Assert.That(result.AtObstacle, Is.True);
        Assert.That(result.UsedFallback, Is.False);
    }

    [Test]
    public void AWideImplement_StillTurnsClearOfTheObstacle()
    {
        // 16 m passes with a 6 m radius: wider than twice the radius, AgOpenGPS's wide turn. The
        // next pass (E = -16) misses the obstacle, so the turn can't follow its turn line; the
        // omega fallback has to keep clear all the same.
        ConfigurationStore.Instance.Tool.SetSectionWidth(0, 1600);

        var result = Turn(Field(ObstacleAction.Turn));

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10));
        Assert.That(result.AtObstacle, Is.True);
        Assert.That(result.Path!.Max(p => p.Northing), Is.LessThanOrEqualTo(ObstacleSouth - Distance + 0.2));
    }

    [Test]
    public void ATurnWhoseExitRunsTheImplementIntoAHardObstacle_IsNotMadeThere()
    {
        // The turn comes out onto the next pass (E = -3, a 6 m implement) beside a hard ditch whose
        // edge is 2.5 m from that pass: the implement would be 0.5 m in it as the turn ends. The
        // tractor itself stays outside the ditch's turn line (1 m), so only the implement says no:
        // the turn is seated further back until its exit clears the ditch, or not made at all.
        var boundary = Field(ObstacleAction.Turn);
        var ditch = Rect(-30, 0, -5.5, 30);
        ditch.AtObstacle = ObstacleAction.Turn;
        ditch.IsHard = true;
        boundary.InnerBoundaries.Add(ditch);

        var result = Turn(boundary);

        Assert.That(result.AtObstacle, Is.True);
        if (result.Path != null)
        {
            // Made further back: the implement clear of the ditch over the turn and the 20 m after it.
            var c = ConfigurationStore.Instance;
            var path = new List<Vec3>(result.Path);
            Vec3 a = path[^2], b = path[^1];
            double len = GeometryMath.Distance(a, b);
            for (double d = 0.5; d <= 20; d += 0.5)
                path.Add(new Vec3(b.Easting + (b.Easting - a.Easting) / len * d, b.Northing + (b.Northing - a.Northing) / len * d, b.Heading));
            var fence = ditch.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            var swept = ImplementSweptPath.Compute(path, new ToolGeometry(
                ToolMount.RearFixed, Width: 6, Offset: 0, VehicleHitchLength: c.Vehicle.HitchLength,
                ToolHitchLength: c.Tool.HitchLength, TrailingHitchLength: 0, TrailingToolToPivotLength: 0,
                TankTrailingHitchLength: 0, Length: c.Tool.Length));
            foreach (var corner in swept.FrontLeft.Concat(swept.FrontRight).Concat(swept.RearLeft).Concat(swept.RearRight))
                Assert.That(GeometryMath.IsPointInPolygon(fence, new Vec2(corner.Easting, corner.Northing)), Is.False,
                    $"implement corner ({corner.Easting:F1},{corner.Northing:F1}) in the ditch");
        }

        // The ditch 2.5 m further off (the implement 2 m clear, more than the 1 m safety margin; the
        // pass's pivot a metre outside the ditch's turn line, which stands the implement's reach of 3 m
        // plus the metre off a hard obstacle): the turn is made.
        boundary.InnerBoundaries.Remove(ditch);
        var further = Rect(-30, 0, -8, 30);
        further.AtObstacle = ObstacleAction.Turn;
        further.IsHard = true;
        boundary.InnerBoundaries.Add(further);

        result = Turn(boundary);

        Assert.That(result.Path, Is.Not.Null.And.Count.GreaterThan(10));
        Assert.That(result.AtObstacle, Is.True);
        Assert.That(result.AtObstacleIndex, Is.EqualTo(0));
    }

    [Test]
    public void AHardObstacle_KeepsTheImplementOutOfIt()
    {
        var c = ConfigurationStore.Instance;
        c.Tool.SetSectionWidth(0, 1200); // 12 m boom, 3-point mounted
        c.Tool.HitchLength = 3;
        c.Tool.IsToolRearFixed = true;
        var boundary = Field(ObstacleAction.Turn, hard: true);

        var result = Turn(boundary);

        if (result.Path == null)
        {
            Assert.That(result.ClearanceBlocked, Is.True, "no path means the operator is warned");
            return;
        }
        Assert.That(result.AtObstacle, Is.True);
        var fence = boundary.InnerBoundaries[0].Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
        var swept = ImplementSweptPath.Compute(result.Path, new ToolGeometry(
            ToolMount.RearFixed, Width: 12, Offset: 0, VehicleHitchLength: c.Vehicle.HitchLength,
            ToolHitchLength: 3, TrailingHitchLength: 0, TrailingToolToPivotLength: 0,
            TankTrailingHitchLength: 0, Length: c.Tool.Length));
        foreach (var corner in swept.FrontLeft.Concat(swept.FrontRight).Concat(swept.RearLeft).Concat(swept.RearRight))
            Assert.That(GeometryMath.IsPointInPolygon(fence, new Vec2(corner.Easting, corner.Northing)), Is.False,
                $"implement corner ({corner.Easting:F1},{corner.Northing:F1}) inside the hard obstacle");
    }

    [Test]
    public void AChangedChoiceOnTheSameBoundary_IsSeenByTheNextTurn()
    {
        var boundary = Field(ObstacleAction.Turn);
        Assert.That(Turn(boundary).AtObstacle, Is.True);

        boundary.InnerBoundaries[0].AtObstacle = ObstacleAction.DriveThrough;

        Assert.That(Turn(boundary).AtObstacle, Is.False, "the cached turn lines follow the choice");
    }
}

/// <summary>
/// The turn at an obstacle through the state machine, cycle by cycle as the pipeline ticks it: the
/// tractor drives its pass toward the obstacle, the turn is planned and taken before it, and it
/// lands on the next pass going back. A turn missed there is not turned from where the tractor is.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class ObstacleUTurnDriveTests
{
    private const double Step = 0.28; // 10 km/h at 10 Hz
    private YouTurnStateMachine _machine = null!;
    private Boundary _boundary = null!;
    private Models.Track.Track _track = null!;
    private GuidanceWorkingState _guidance = null!;
    private YouTurnWorkingState _turn = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.AntennaPivot = 0;
        c.Vehicle.AntennaOffset = 0;
        c.Vehicle.Wheelbase = 2.5;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600);
        c.Guidance.UTurnDistanceFromBoundary = 1.0;
        c.Guidance.UTurnRadius = 6.0;
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        _machine = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

        _boundary = new Boundary { OuterBoundary = Rect(-100, -150, 100, 150) };
        var obstacle = Rect(-10, 40, 10, 60);
        obstacle.AtObstacle = ObstacleAction.Turn;
        _boundary.InnerBoundaries.Add(obstacle);
        _track = Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));
        _guidance = new GuidanceWorkingState();
        _turn = new YouTurnWorkingState { IsEnabled = true };
    }

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    private static BoundaryPolygon Rect(double e0, double n0, double e1, double n1)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e1, n0), (e1, n1), (e0, n1) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private YouTurnEffects Tick(double e, double n, double headingRad)
    {
        var ctx = new YouTurnStateMachine.TickContext(
            new Position { Easting = e, Northing = n, Heading = headingRad * 180 / System.Math.PI },
            _track, _boundary,
            new List<Vec3> { new(-80, -130, 0), new(80, -130, 0), new(80, 130, 0), new(-80, 130, 0) },
            UTurnSkipRows: 0, IsSkipWorkedMode: false, HeadlandCalculatedWidth: 20, HeadlandDistance: 20);
        _turn.YouTurnCounter++;
        return _machine.Tick(in ctx, _guidance, _turn);
    }

    [Test]
    public void TheTractor_TurnsBeforeTheObstacle_OntoTheNextPassBack()
    {
        double n = 0;
        while (_turn.TurnPath == null && n < 39) { Tick(3, n, 0); n += Step; }
        Assert.That(_turn.TurnPath, Is.Not.Null, "a turn is planned on the way to the obstacle");
        Assert.That(_turn.TurnAtObstacle, Is.True);
        Assert.That(_turn.TurnPath!.Max(p => p.Northing), Is.LessThanOrEqualTo(39.2));

        while (!_turn.IsExecuting && n < 39) { Tick(3, n, 0); n += Step; }
        Assert.That(_turn.IsExecuting, Is.True, "the turn starts before the obstacle");

        // Drive the turn.
        var path = _turn.TurnPath!;
        double along = 0, e = 3, heading = 0;
        int startPass = _guidance.HowManyPathsAway;
        for (int i = 0; i < 2000 && _turn.IsExecuting; i++)
        {
            along += Step;
            (e, n, heading) = At(path, along);
            Tick(e, n, heading);
        }

        Assert.That(_turn.IsExecuting, Is.False, "the turn completes");
        Assert.That(System.Math.Abs(_guidance.HowManyPathsAway - startPass), Is.EqualTo(1), "onto the next pass");
        Assert.That(System.Math.Cos(heading), Is.LessThan(-0.9), "going back south");
    }

    [Test]
    public void ATurnMissedBeforeTheObstacle_IsNotTurnedFromHere()
    {
        double n = 0;
        while (_turn.TurnPath == null && n < 39) { Tick(3, n, 0); n += Step; }
        Assert.That(_turn.TurnAtObstacle, Is.True);
        var arcStart = _turn.ArcStart ?? _turn.TurnPath![0];

        // The operator steered 2 m off the pass and past where the arc leaves it.
        var effects = Tick(5, arcStart.Northing + 1, 0);

        Assert.That(_turn.IsExecuting, Is.False, "no turn from here: it would swing into the obstacle");
        Assert.That(effects.StatusMessage, Does.Contain("obstacle"));
    }

    [Test]
    public void AnObstacleMovedOntoThePass_AfterTheTurnWasPlanned_IsTurnedAt()
    {
        // The obstacle beside the pass at first: the turn is planned at the field's edge.
        var obstacle = _boundary.InnerBoundaries[0];
        void Place(double e0, double e1)
        {
            obstacle.Points.Clear();
            foreach (var (e, n) in new[] { (e0, 40.0), (e1, 40.0), (e1, 60.0), (e0, 60.0) })
                obstacle.Points.Add(new BoundaryPoint(e, n, 0));
            obstacle.UpdateBounds();
        }
        Place(30, 50);
        double n = 0;
        while (_turn.TurnPath == null && n < 20) { Tick(3, n, 0); n += Step; }
        Assert.That(_turn.TurnPath, Is.Not.Null);
        Assert.That(_turn.TurnAtObstacle, Is.False);

        Place(-10, 10); // moved: the same number of corners, as a circle that is moved
        Tick(3, n, 0);

        Assert.That(_turn.TurnAtObstacle, Is.True, "planned again for the obstacle where it is now");
    }

    [Test]
    public void WithNoTurnPlanned_AUTurnObstacleAhead_IsReported()
    {
        var ctx = Ctx(3, 25, 0);
        Assert.That(_machine.ObstacleAheadWithoutTurn(in ctx, _turn, 16), Is.EqualTo(0), "14 m to its turn line");
        Assert.That(_machine.ObstacleAheadWithoutTurn(in ctx, _turn, 10), Is.EqualTo(-1), "beyond the look-ahead");
        var away = Ctx(3, 25, System.Math.PI);
        Assert.That(_machine.ObstacleAheadWithoutTurn(in away, _turn, 16), Is.EqualTo(-1), "behind the tractor");

        _boundary.InnerBoundaries[0].AtObstacle = ObstacleAction.DriveThrough;
        Assert.That(_machine.ObstacleAheadWithoutTurn(in ctx, _turn, 16), Is.EqualTo(-1), "driven through");
    }

    [Test]
    public void AnObstacleChangedUnderTheTurn_IsReported_WhenTheRestOfTheTurnRunsIntoIt()
    {
        double n = 0;
        while (!_turn.IsExecuting && n < 39) { Tick(3, n, 0); n += Step; }
        Assert.That(_turn.IsExecuting, Is.True, "the turn before the obstacle is being driven");
        Assert.That(_turn.TurnAtObstacleIndex, Is.EqualTo(0));
        var ctx = Ctx(3, n, 0);
        Assert.That(_machine.ObstacleChangedUnderTurn(in ctx, _turn), Is.EqualTo(-1), "nothing changed");

        var far = Rect(60, 100, 70, 110);
        far.AtObstacle = ObstacleAction.Turn;
        _boundary.InnerBoundaries.Add(far);
        Assert.That(_machine.ObstacleChangedUnderTurn(in ctx, _turn), Is.EqualTo(-1), "changed, but the turn is clear of it");
        Assert.That(_machine.ObstacleChangedUnderTurn(in ctx, _turn), Is.EqualTo(-1), "each change is judged once");

        // A ditch across the next pass (E = -3), where the turn comes out.
        var across = Rect(-8, 20, 2, 25);
        across.AtObstacle = ObstacleAction.Turn;
        across.IsHard = true;
        _boundary.InnerBoundaries.Add(across);
        Assert.That(_machine.ObstacleChangedUnderTurn(in ctx, _turn), Is.EqualTo(2), "the exit runs into it");
        Assert.That(_turn.IsExecuting, Is.True, "the turn itself is left to the caller");
    }

    [Test]
    public void WithATurnPlanned_TheObstacleAhead_IsNotReported()
    {
        double n = 0;
        while (_turn.TurnPath == null && n < 30) { Tick(3, n, 0); n += Step; }
        var ctx = Ctx(3, 25, 0);
        Assert.That(_machine.ObstacleAheadWithoutTurn(in ctx, _turn, 16), Is.EqualTo(-1));
    }

    [TestCase(30.0)]
    [TestCase(33.0)]
    public void AManualTurn_ThatWouldCrossTheObstacle_IsRefused(double northing)
    {
        var ctx = Ctx(3, northing, 0);
        var effects = _machine.TriggerManual(turnLeft: true, isAutoSteerEngaged: true, in ctx, _guidance, _turn);

        Assert.That(_turn.IsExecuting, Is.False);
        Assert.That(effects.StatusMessage, Does.Contain("obstacle"));
    }

    [Test]
    public void AManualTurn_ClearOfTheObstacle_IsMade()
    {
        var ctx = Ctx(3, 0, 0);
        _machine.TriggerManual(turnLeft: true, isAutoSteerEngaged: true, in ctx, _guidance, _turn);
        Assert.That(_turn.IsExecuting, Is.True);
    }

    private YouTurnStateMachine.TickContext Ctx(double e, double n, double headingRad) => new(
        new Position { Easting = e, Northing = n, Heading = headingRad * 180 / System.Math.PI },
        _track, _boundary,
        new List<Vec3> { new(-80, -130, 0), new(80, -130, 0), new(80, 130, 0), new(-80, 130, 0) },
        UTurnSkipRows: 0, IsSkipWorkedMode: false, HeadlandCalculatedWidth: 20, HeadlandDistance: 20);

    private static (double E, double N, double Heading) At(List<Vec3> path, double along)
    {
        double walked = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double de = path[i].Easting - path[i - 1].Easting, dn = path[i].Northing - path[i - 1].Northing;
            double len = System.Math.Sqrt(de * de + dn * dn);
            if (len < 1e-9) continue;
            if (walked + len >= along)
            {
                double t = (along - walked) / len;
                return (path[i - 1].Easting + t * de, path[i - 1].Northing + t * dn, System.Math.Atan2(de, dn));
            }
            walked += len;
        }
        var last = path[^1];
        var prev = path[^2];
        double h = System.Math.Atan2(last.Easting - prev.Easting, last.Northing - prev.Northing);
        double over = along - walked;
        return (last.Easting + over * System.Math.Sin(h), last.Northing + over * System.Math.Cos(h), h);
    }
}

