// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// #306: an automatic turn starts when the tractor is ON the planned path, heading along
/// it, wherever it joins. The planned path's entry leg is the current pass itself, so a turn
/// armed late (its first point already behind the tractor) is taken all the same. A path the
/// tractor cannot join any more is replanned from where it is, or dropped with a warning;
/// it never stays on screen while the tractor drives into the headland.
/// </summary>
[TestFixture]
public class YouTurnTriggerOnPathTests
{
    // ── The rule, on hand-built paths ────────────────────────────────────

    [Test]
    public void OnTheEntryLeg_HeadingAlongIt_TheTurnStarts()
    {
        var sm = BuildStateMachine();
        var turn = new YouTurnWorkingState { TurnPath = LegThenArc(), ArcStart = new Vec3(0, 20, 0) };

        // 8 m along a leg that began at (0,0): the old 2 m rendezvous with the first point is long gone.
        var effects = sm.Tick(Ctx(0, 8, headingDeg: 0), new GuidanceWorkingState(), turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsTriggered, Is.True);
            Assert.That(turn.IsExecuting, Is.True);
            Assert.That(effects.StatusMessage, Is.EqualTo("YouTurn triggered!"));
        });
    }

    [Test]
    public void BesideThePath_NoTrigger()
    {
        var sm = BuildStateMachine();
        var turn = new YouTurnWorkingState { TurnPath = LegThenArc(), ArcStart = new Vec3(0, 20, 0) };

        sm.Tick(Ctx(3, 8, headingDeg: 0), new GuidanceWorkingState(), turn);

        Assert.That(turn.IsTriggered, Is.False, "3 m off the line is not on the path");
        Assert.That(turn.TurnPath, Is.Not.Null, "still armed: the tractor may come back onto the line");
    }

    [Test]
    public void OnTheLineButHeadingTheOtherWay_NoTrigger()
    {
        var sm = BuildStateMachine();
        var turn = new YouTurnWorkingState { TurnPath = LegThenArc(), ArcStart = new Vec3(0, 20, 0) };

        sm.Tick(Ctx(0, 8, headingDeg: 180), new GuidanceWorkingState(), turn);

        Assert.That(turn.IsTriggered, Is.False, "the exit leg of a turn runs the other way; heading must match");
    }

    [Test]
    public void ShortOfThePath_NoTriggerYet_AndTheCountdownIsPublished()
    {
        var sm = BuildStateMachine();
        var turn = new YouTurnWorkingState { TurnPath = LegThenArc(), ArcStart = new Vec3(0, 20, 0) };

        sm.Tick(Ctx(0, -12, headingDeg: 0), new GuidanceWorkingState(), turn);

        Assert.That(turn.IsTriggered, Is.False);
        Assert.That(turn.DistanceToTrigger, Is.EqualTo(12.0).Within(0.01));
    }

    [Test]
    public void TheApproachAlarm_MeasuresToTheArc_NotToTheLegStart()
    {
        var sm = BuildStateMachine();
        // Leg from (0,0), arc at (0,20). 18 m before the LEG start is 38 m from the arc: no alarm.
        var turn = new YouTurnWorkingState { TurnPath = LegThenArc(), ArcStart = new Vec3(0, 20, 0) };

        var far = sm.Tick(Ctx(0, -18, headingDeg: 0), new GuidanceWorkingState(), turn);
        Assert.That(far.ApproachAlarmSound, Is.False, "38 m from the arc");

        var near = sm.Tick(Ctx(0, 2, headingDeg: 0), new GuidanceWorkingState(), turn);
        Assert.That(near.ApproachAlarmSound, Is.True, "18 m from the arc");
    }

    [Test]
    public void InTheHeadlandOffThePath_TheTurnIsDroppedWithAWarning()
    {
        var sm = BuildStateMachine();
        // A path on another line entirely; the tractor is in the headland (between the ±45
        // turn line and the ±50 fence) and never joined it.
        var turn = new YouTurnWorkingState { TurnPath = LegThenArc(easting: 20), ArcStart = new Vec3(20, 20, 0) };

        var effects = sm.Tick(Ctx(0, 47, headingDeg: 0), new GuidanceWorkingState(), turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.TurnPath, Is.Null, "a missed turn must not stay on screen");
            Assert.That(turn.IsTriggered, Is.False);
            Assert.That(effects.TurnCreationFailedSound, Is.True, "the operator must hear that the turn is missed");
            Assert.That(effects.StatusMessage, Does.Contain("missed"));
            Assert.That(effects.SyncTurnPathToMap, Is.True);
        });
    }

    [Test]
    public void ArcAlreadyBehindTheTractor_TurnsFromHereInstead()
    {
        var sm = BuildStateMachine();
        // The planned arc left the line at (0,20) and the tractor, 2 m off that line, is at
        // y = 30: it cannot join the path any more, but there is room to turn from here.
        var turn = new YouTurnWorkingState
        {
            TurnPath = LegThenArc(), ArcStart = new Vec3(0, 20, 0), IsTurnLeft = false,
        };
        var planned = turn.TurnPath;

        var effects = sm.Tick(Ctx(2, 30, headingDeg: 0), new GuidanceWorkingState(), turn);

        Assert.Multiple(() =>
        {
            Assert.That(turn.IsExecuting, Is.True, "replanned from the tractor and started");
            Assert.That(turn.TurnPath, Is.Not.SameAs(planned));
            Assert.That(turn.TurnPath!.Count, Is.GreaterThan(2));
            Assert.That(effects.StatusMessage, Does.Contain("from here"));
        });
    }

    // ── Through the real planner: renatorgr's turn (#306) ────────────────

    /// <summary>
    /// The turn is armed 15 m before the turn line, which puts the planned path's first
    /// point behind the tractor (16 m entry leg plus the arc's own setback). The tractor
    /// is on the entry leg, so the turn must start before it reaches the headland.
    /// </summary>
    [Test]
    public void ArmedLate_OnTheLeg_TheTurnStartsBeforeTheHeadland()
    {
        var (sm, ctxAt) = BuildPlannerBench();
        var guidance = new GuidanceWorkingState();
        var turn = new YouTurnWorkingState { IsEnabled = true };

        double y = 30; // turn line at y = 45
        bool pathWasBehind = false;
        for (int i = 0; i < 40 && !turn.IsExecuting; i++, y += 0.5)
        {
            turn.YouTurnCounter++;
            sm.Tick(ctxAt(0, y, 0), guidance, turn);
            if (turn.TurnPath != null && turn.TurnPath[0].Northing < y) pathWasBehind = true;
            Assert.That(turn.CurrentZone, Is.EqualTo(TractorZone.InCultivatedArea),
                $"reached the headland at y={y:F1} without the turn starting");
        }

        Assert.That(turn.IsExecuting, Is.True, "the turn must start while the tractor is still on the pass");
        Assert.That(pathWasBehind, Is.True, "the scenario: the path's first point was behind the tractor when armed");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>A 20 m leg north from (easting, 0), then a quarter arc of 8 m radius to the east.</summary>
    private static List<Vec3> LegThenArc(double easting = 0)
    {
        var path = new List<Vec3>();
        for (double n = 0; n <= 20; n += 1) path.Add(new Vec3(easting, n, 0));
        for (int k = 1; k <= 9; k++)
        {
            double a = k * Math.PI / 18; // 10° steps
            path.Add(new Vec3(easting + 8 - 8 * Math.Cos(a), 20 + 8 * Math.Sin(a), a));
        }
        return path;
    }

    private static YouTurnStateMachine BuildStateMachine()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600);
        c.Guidance.UTurnRadius = 8.0;
        var polygonOffset = Substitute.For<IPolygonOffsetService>();
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, polygonOffset, c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        return new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);
    }

    /// <summary>100 m square fence at ±50, turn line at ±45, AB line north along x = 0.</summary>
    private static YouTurnStateMachine.TickContext Ctx(double e, double n, double headingDeg)
    {
        var outer = new BoundaryPolygon
        {
            Points = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) }
                .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        };
        return new YouTurnStateMachine.TickContext(
            new Position { Easting = e, Northing = n, Heading = headingDeg, Speed = 2.5 },
            Models.Track.Track.FromABLine("AB-test", new Vec3(0, -100, 0), new Vec3(0, 100, 0)),
            new Boundary { OuterBoundary = outer },
            new List<Vec3> { new(-45, -45, 0), new(45, -45, 0), new(45, 45, 0), new(-45, 45, 0) },
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 5.0, HeadlandDistance: 5.0);
    }

    /// <summary>
    /// The real planner on a 120 m square field: fence at ±60, turn line 15 m inside it,
    /// 6 m tool, 8 m radius, 16 m legs; an AB line north through the middle.
    /// </summary>
    private static (YouTurnStateMachine, Func<double, double, double, YouTurnStateMachine.TickContext>) BuildPlannerBench()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = 2.5;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600);
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnStyle = 0;

        var offset = new PolygonOffsetService();
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        var sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);

        var outer = new List<Vec2> { new(-60, -60), new(60, -60), new(60, 60), new(-60, 60) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = outer.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        var turnLine = offset.CalculatePointHeadings(offset.CreateInwardOffset(outer, 15.0)!);
        var track = Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        YouTurnStateMachine.TickContext CtxAt(double e, double n, double headingDeg) => new(
            new Position { Easting = e, Northing = n, Heading = headingDeg, Speed = 2.78 },
            track, boundary, turnLine,
            UTurnSkipRows: 0, IsSkipWorkedMode: false,
            HeadlandCalculatedWidth: 15.0, HeadlandDistance: 15.0);
        return (sm, CtxAt);
    }
}
