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
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// Driving on across a narrow part of the headland instead of turning there
/// (<see cref="GuidanceConfig.UTurnCrossGap"/>). The field is a rectangle with a waist:
/// for 20 m its west edge steps in to x = 2, so the pass (x = 3: the AB line at x = 0 is
/// the edge of pass 0, the tool is 6 m) stays 1 m inside the field but leaves the turn
/// area (2 m inside the edge) for about 24 m and comes back into the lobe beyond. Closed loop: the state machine, the track guidance and a bicycle
/// model, as the operator would drive it.
/// </summary>
[TestFixture]
[NonParallelizable]
public class HeadlandGapCrossingTests
{
    private const double Dt = 0.1;
    private const double SpeedMs = 2.78;
    private const double Wheelbase = 2.5;
    private const double PassEasting = 3.0;        // pass 0 of the AB line at x = 0, tool 6 m
    private const double GapStartNorthing = -12.0; // the lower lobe's turn line at the pass
    private const double GapEndNorthing = 12.0;    // the upper lobe's turn line at the pass

    private sealed record Run(
        List<Vec3>? FirstPlannedPath,
        bool PathKeptThroughTheGap,
        bool DroveThroughTheGapInHeadland,
        double NorthingWhenTriggered,
        double EastingWhenTriggered);

    /// <summary>A 100 × 160 m field whose west edge steps in to <paramref name="waistWest"/> between y = -10 and 10.</summary>
    private static Boundary WaistedField(double waistWest, IEnumerable<BoundaryPolygon>? inner = null)
    {
        var ring = new List<Vec2>
        {
            new(-50, -80), new(50, -80), new(50, -10), new(30, -10), new(30, 10), new(50, 10),
            new(50, 80), new(-50, 80), new(-50, 10), new(waistWest, 10), new(waistWest, -10), new(-50, -10),
        };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = ring.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            },
        };
        if (inner != null) boundary.InnerBoundaries.AddRange(inner);
        boundary.OuterBoundary.UpdateBounds();
        foreach (var b in boundary.InnerBoundaries) b.UpdateBounds();
        return boundary;
    }

    /// <summary>The headland line, 10 m in: the lobes end 10 m before the waist, whose own
    /// headland is pinched to x 12..20, so the pass at x = 3 is in the headland from y = -20 to 20.</summary>
    private static List<Vec3> WaistedHeadland() => new()
    {
        new(-40, -70, 0), new(40, -70, 0), new(40, -20, 0), new(20, -20, 0), new(20, 20, 0), new(40, 20, 0),
        new(40, 70, 0), new(-40, 70, 0), new(-40, 20, 0), new(12, 20, 0), new(12, -20, 0), new(-40, -20, 0),
    };

    private static Run Drive(Boundary boundary, double crossGap)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Vehicle.Wheelbase = Wheelbase;
        c.Vehicle.MaxSteerAngle = 35;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 600); // 6 m
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 16.0;
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnCrossGap = crossGap;

        var offset = new PolygonOffsetService();
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, offset, c);
        var pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, c);
        var sm = new YouTurnStateMachine(creation, pathing, NullLogger<YouTurnStateMachine>.Instance, c);
        var trackGuidance = new AgOpenWeb.Services.Track.TrackGuidanceService();

        var headland = WaistedHeadland();
        // Off the 2 m grid of the densified pass, so that no point lies exactly on the turn line.
        var track = Models.Track.Track.FromABLine("ab", new Vec3(0, -100.7, 0), new Vec3(0, 99.3, 0));
        // The pipeline steers to the offset pass, not to the AB line: here that pass is a track of its own.
        var pass = Models.Track.Track.FromABLine("pass", new Vec3(PassEasting, -100.7, 0), new Vec3(PassEasting, 99.3, 0));

        var gState = new GuidanceWorkingState();
        var tState = new YouTurnWorkingState { IsEnabled = true };

        var pivot = new Vec2(PassEasting, -60);
        double heading = 0;
        double speedKmh = SpeedMs * 3.6;
        double lookAhead = Math.Max(c.Guidance.MinLookAheadDistance,
            c.Guidance.GoalPointLookAheadHold + speedKmh * c.Guidance.GoalPointLookAheadMult * 0.1);

        List<Vec3>? firstPlanned = null;
        bool pathKept = true;
        bool inHeadlandInGap = false;
        double triggeredAt = double.NaN;

        for (int i = 0; i < 900; i++)
        {
            var pos = new Position { Easting = pivot.Easting, Northing = pivot.Northing, Heading = heading * 180 / Math.PI, Speed = SpeedMs };
            var ctx = new YouTurnStateMachine.TickContext(pos, track, boundary, headland,
                UTurnSkipRows: 0, IsSkipWorkedMode: false, HeadlandCalculatedWidth: 10.0, HeadlandDistance: 5.0);
            tState.YouTurnCounter++;
            sm.Tick(in ctx, gState, tState);

            if (firstPlanned == null && tState.TurnPath != null && tState.TurnPath.Count > 2)
                firstPlanned = new List<Vec3>(tState.TurnPath);
            else if (firstPlanned != null && !tState.IsExecuting && tState.TurnPath == null)
                pathKept = false;

            bool inGap = pivot.Northing > GapStartNorthing + 1 && pivot.Northing < GapEndNorthing - 1;
            if (inGap && tState.CurrentZone == TractorZone.InHeadland) inHeadlandInGap = true;

            if (tState.IsExecuting)
            {
                triggeredAt = pivot.Northing;
                break;
            }

            var tin = new Models.Track.TrackGuidanceInput
            {
                Track = pass,
                PivotPosition = new Vec3(pivot.Easting, pivot.Northing, heading),
                SteerPosition = new Vec3(pivot.Easting + Math.Sin(heading) * Wheelbase,
                                         pivot.Northing + Math.Cos(heading) * Wheelbase, heading),
                UseStanley = false, Wheelbase = Wheelbase, MaxSteerAngle = 35,
                GoalPointDistance = lookAhead, IsHeadingSameWay = true, FixHeading = heading,
            };
            double steerRad = trackGuidance.CalculateGuidance(tin).SteerAngle * Math.PI / 180.0;
            heading += (SpeedMs / Wheelbase) * Math.Tan(steerRad) * Dt;
            pivot = new Vec2(pivot.Easting + SpeedMs * Dt * Math.Sin(heading),
                             pivot.Northing + SpeedMs * Dt * Math.Cos(heading));
            if (pivot.Northing > 85) break;
        }

        if (firstPlanned != null)
            TestContext.WriteLine($"planned: {firstPlanned.Count} points, start ({firstPlanned[0].Easting:F1}, {firstPlanned[0].Northing:F1}), apex {Apex(firstPlanned):F1}, end ({firstPlanned[^1].Easting:F1}, {firstPlanned[^1].Northing:F1}); triggered at ({pivot.Easting:F1}, {triggeredAt:F1})");
        return new Run(firstPlanned, pathKept, inHeadlandInGap, triggeredAt, pivot.Easting);
    }

    private static double Apex(List<Vec3> path) => path.Max(p => p.Northing);

    [Test]
    public void A_gap_within_the_setting_is_driven_across_and_the_turn_is_planned_beyond_it()
    {
        var run = Drive(WaistedField(2), crossGap: 30);

        Assert.That(run.FirstPlannedPath, Is.Not.Null, "no turn was planned");
        Assert.That(Apex(run.FirstPlannedPath!), Is.GreaterThan(60),
            "the turn was planned at the gap, not at the far end of the field");
        Assert.That(run.EastingWhenTriggered, Is.EqualTo(PassEasting).Within(0.5), "the tractor left the pass");
        Assert.That(run.DroveThroughTheGapInHeadland, Is.True, "the tractor never crossed the gap in the headland");
        Assert.That(run.PathKeptThroughTheGap, Is.True, "the planned turn was dropped while crossing the gap");
        Assert.That(run.NorthingWhenTriggered, Is.GreaterThan(GapEndNorthing), "the turn did not start beyond the gap");
    }

    [Test]
    public void With_the_setting_off_the_pass_is_turned_at_the_gap()
    {
        var run = Drive(WaistedField(2), crossGap: 0);

        Assert.That(run.FirstPlannedPath, Is.Not.Null, "no turn was planned");
        Assert.That(Apex(run.FirstPlannedPath!), Is.LessThan(GapStartNorthing + 1));
        Assert.That(run.NorthingWhenTriggered, Is.LessThan(GapStartNorthing));
    }

    [Test]
    public void A_gap_longer_than_the_setting_is_turned_at()
    {
        var run = Drive(WaistedField(2), crossGap: 20); // the gap is about 24 m

        Assert.That(run.FirstPlannedPath, Is.Not.Null, "no turn was planned");
        Assert.That(Apex(run.FirstPlannedPath!), Is.LessThan(GapStartNorthing + 1));
    }

    [Test]
    public void A_gap_that_leaves_the_field_is_not_crossed()
    {
        // The waist's west edge at x = 4: the pass at x = 3 would be outside the field.
        var run = Drive(WaistedField(4), crossGap: 30);

        Assert.That(run.FirstPlannedPath, Is.Not.Null, "no turn was planned");
        Assert.That(Apex(run.FirstPlannedPath!), Is.LessThan(GapStartNorthing + 1));
    }

    [Test]
    public void A_gap_through_an_obstacle_is_not_crossed()
    {
        var obstacle = new BoundaryPolygon
        {
            Points = new List<Vec2> { new(2, -2), new(6, -2), new(6, 2), new(2, 2) }
                .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        };
        var run = Drive(WaistedField(2, new[] { obstacle }), crossGap: 30);

        Assert.That(run.FirstPlannedPath, Is.Not.Null, "no turn was planned");
        Assert.That(Apex(run.FirstPlannedPath!), Is.LessThan(GapStartNorthing + 1));
    }

    [Test]
    public void A_drive_through_obstacle_in_the_gap_does_not_stop_the_crossing()
    {
        var obstacle = new BoundaryPolygon
        {
            IsDriveThrough = true,
            Points = new List<Vec2> { new(2, -2), new(6, -2), new(6, 2), new(2, 2) }
                .Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
        };
        var run = Drive(WaistedField(2, new[] { obstacle }), crossGap: 30);

        Assert.That(run.FirstPlannedPath, Is.Not.Null, "no turn was planned");
        Assert.That(Apex(run.FirstPlannedPath!), Is.GreaterThan(60));
    }

    [Test]
    public void The_exit_leg_lands_on_the_next_pass_beyond_the_gap_when_that_pass_is_outside_the_field_abreast_of_it()
    {
        // The next pass is at x = -3: outside the field for the length of the waist. The exit
        // leg must still come down the next pass from the far end, not from the gap.
        var run = Drive(WaistedField(2), crossGap: 30);

        Assert.That(run.FirstPlannedPath, Is.Not.Null, "no turn was planned");
        var path = run.FirstPlannedPath!;
        Assert.That(Apex(path), Is.GreaterThan(60), "the turn was not planned at the far end");
        var end = path[^1];
        Assert.That(end.Easting, Is.EqualTo(-3).Within(1.0), "the exit leg does not end on the next pass");
        Assert.That(end.Northing, Is.GreaterThan(GapEndNorthing), "the exit leg ends in or before the gap");
    }
}
