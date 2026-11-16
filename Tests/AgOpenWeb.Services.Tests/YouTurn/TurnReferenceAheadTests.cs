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
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A turn is built from the headland crossing AHEAD of the tractor. The AB reference point was
/// the crossing closest to the tractor, ahead or behind: entering an olive grove at a corner the
/// headland cut 11 m off the first pass, the closest crossing was the one just passed, the turn
/// was built from it, seated 4 m back to clear the hard boundary, started under the tractor and
/// turned it round at the entry, out of the field. A turn seated back behind the tractor to
/// clear a hard boundary is refused.
/// </summary>
[TestFixture, NonParallelizable]
public class TurnReferenceAheadTests
{
    // Headland ring: a square ±40 with a bay cut into its west side from y = -27 to y = -10,
    // reaching x = 5. The AB line x = 0 is inside from y = -40 to -27 (a 13 m clip), in the
    // bay to -10, inside again to 40.
    private static readonly List<Vec3> Headland = new()
    {
        new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0), new(-40, 40, 0),
        new(-40, -10, 0), new(5, -10, 0), new(5, -27, 0), new(-40, -27, 0),
    };

    private static readonly AgOpenWeb.Models.Track.Track Track = AgOpenWeb.Models.Track.Track.FromABLine(
        "AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

    [Test]
    public void The_reference_is_the_crossing_ahead_not_the_closer_one_behind()
    {
        // Heading north at y = -35: the crossing just passed (y = -40) is 5 m behind, the end of
        // the clip (y = -27) 8 m ahead.
        var r = YouTurnCreationService.PassEndAhead(Track, new Vec3(0, -35, 0), Headland, headingSameWay: true)?.Crossing;
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.Northing, Is.EqualTo(-27).Within(1e-6));

        // Heading south at y = -35: the crossing ahead is y = -40.
        r = YouTurnCreationService.PassEndAhead(Track, new Vec3(0, -35, 0), Headland, headingSameWay: false)?.Crossing;
        Assert.That(r!.Value.Northing, Is.EqualTo(-40).Within(1e-6));
    }

    // Driving into the field across the turn line at the start of a job (harness C11, C15, J12,
    // J20, 2026-11-13 on): the crossing under the tractor was taken for the end of the pass, the
    // turn was built there, didn't fit, and AgOpenWeb told the operator to take over with the
    // whole field to do. The end is where the pass leaves the area, as in AgOpenGPS (the walk
    // ahead to the first point outside the turn line).
    [TestCase(-40.5)] // just short of the line
    [TestCase(-40.0)] // on it
    [TestCase(-39.5)] // just over it
    public void Driving_into_the_field_the_crossing_under_the_tractor_is_not_the_end(double y)
    {
        var r = YouTurnCreationService.PassEndAhead(Track, new Vec3(0, y, 0), Headland, headingSameWay: true)?.Crossing;
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.Northing, Is.EqualTo(-27).Within(1e-6));
    }

    [TestCase(-40.5)]
    [TestCase(-40.0)]
    [TestCase(-39.5)]
    public void Driving_into_the_field_on_a_curve_the_crossing_under_the_tractor_is_not_the_end(double y)
    {
        var curve = new AgOpenWeb.Models.Track.Track
        {
            Name = "curve",
            Points = Enumerable.Range(0, 201).Select(i => new Vec3(0, -100 + i, 0)).ToList(),
        };
        var r = YouTurnCreationService.PassEndAhead(curve, new Vec3(0, y, 0), Headland, headingSameWay: true)?.Crossing;
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.Northing, Is.EqualTo(-27).Within(1e-6));
    }

    // The same in the turn builder (FindCurveTurnPoint): walking ahead from the tractor to the first
    // point outside the turn area, the tractor still on the turn line as it drives in found the
    // point under it, and the turn was failed as too late (the crossing within 3 m).
    [TestCase(0.0)]   // mid-field: the control
    [TestCase(-48.5)] // just short of the turn line
    [TestCase(-48.0)] // on it
    [TestCase(-47.5)] // just over it
    public void Driving_into_the_field_the_turn_is_built_at_the_far_end(double y)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 800);
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnRadius = 8.0;
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);

        var fence = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var turnLine = new List<Vec3> { new(-48, -48, 0), new(48, -48, 0), new(48, 48, 0), new(-48, 48, 0) };
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = false, DistanceToHeadland = 48 - y };
        var r = creation.CreateTurnPath(new Position { Easting = 4, Northing = y, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, boundary, turnLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 8, headlandDistance: 48 - y);

        Assert.That(r.Path, Is.Not.Null, "a turn is built");
        Assert.That(r.Path!.Max(p => p.Northing), Is.GreaterThan(40), "at the north end");
        Assert.That(r.Path.Min(p => p.Northing), Is.GreaterThan(0), "not at the south end, where the tractor drives in");
    }

    [Test]
    public void Past_the_last_crossing_there_is_no_end_ahead()
    {
        // Heading north at y = 45, past the north edge: the pass does not leave the area ahead.
        var r = YouTurnCreationService.PassEndAhead(Track, new Vec3(0, 45, 0), Headland, headingSameWay: true);
        Assert.That(r, Is.Null);
    }

    // The clip of the turn line at a notch (field job J19 on Olival, 2026-11-15: pass -18 runs
    // 1.65 m from the notch's tip, 0.35 m outside the 2 m turn line for a few metres; every search
    // took that for the end, 49 m into the pass, and the turn there landed on the pass through the
    // notch). The fence here: the square ±42 with the bay 2 m further out, reaching x = 3.
    private static readonly List<Vec3> Fence = new()
    {
        new(-42, -42, 0), new(42, -42, 0), new(42, 42, 0), new(-42, 42, 0),
        new(-42, -8, 0), new(3, -8, 0), new(3, -29, 0), new(-42, -29, 0),
    };

    [Test]
    public void A_clip_of_the_turn_line_inside_the_fence_is_driven_through()
    {
        // The AB line at x = 4 is 1 m outside the turn line's bay (x < 5) for 17 m and 1 m inside
        // the fence's: the pass goes on, and ends at the north edge.
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(4, -100, 0), new Vec3(4, 100, 0));
        var r = YouTurnCreationService.PassEndAhead(track, new Vec3(4, -35, 0), Headland, headingSameWay: true, fence: Fence);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.Crossing.Northing, Is.EqualTo(40).Within(1e-6), "the end is the north edge, not the clip at y = -27");
        Assert.That(r.Value.Heading, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void A_clip_that_leaves_the_fence_is_the_end_of_the_pass()
    {
        // The AB line at x = 2 is outside the fence in the bay (x < 3): the end is there.
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(2, -100, 0), new Vec3(2, 100, 0));
        var r = YouTurnCreationService.PassEndAhead(track, new Vec3(2, -35, 0), Headland, headingSameWay: true, fence: Fence);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.Crossing.Northing, Is.EqualTo(-27).Within(1e-6));
    }

    [Test]
    public void A_long_stretch_outside_the_turn_line_is_the_end_of_the_pass()
    {
        // The same bay 30 m long (y = -27 to 3), inside the fence all the way: longer than the
        // landing length, so the end of the pass, as AgOpenGPS has it.
        var headland = new List<Vec3>
        {
            new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0), new(-40, 40, 0),
            new(-40, 3, 0), new(5, 3, 0), new(5, -27, 0), new(-40, -27, 0),
        };
        var fence = new List<Vec3>
        {
            new(-42, -42, 0), new(42, -42, 0), new(42, 42, 0), new(-42, 42, 0),
            new(-42, 5, 0), new(3, 5, 0), new(3, -29, 0), new(-42, -29, 0),
        };
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(4, -100, 0), new Vec3(4, 100, 0));
        var r = YouTurnCreationService.PassEndAhead(track, new Vec3(4, -35, 0), headland, headingSameWay: true, fence: fence);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.Crossing.Northing, Is.EqualTo(-27).Within(1e-6));
    }

    [Test]
    public void Without_the_fence_every_point_outside_the_turn_line_is_an_end()
    {
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(4, -100, 0), new Vec3(4, 100, 0));
        var r = YouTurnCreationService.PassEndAhead(track, new Vec3(4, -35, 0), Headland, headingSameWay: true);
        Assert.That(r!.Value.Crossing.Northing, Is.EqualTo(-27).Within(1e-6));
    }

    [Test]
    public void The_turn_builder_drives_through_a_clip_and_builds_the_turn_at_the_far_end()
    {
        // A notch in the west fence from y = -20 to -10 reaching x = 1; the turn line's notch (2 m
        // in) reaches x = 3. The pass at x = 0 is 3 m outside the turn line for 10 m and 1 m inside
        // the fence (J19's shape): the turn is built at the north end, not at the notch.
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Overlap = 0;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 800);
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnRadius = 8.0;
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);

        var fence = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50), new(-50, -10), new(1, -10), new(1, -20), new(-50, -20) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var turnLine = new List<Vec3> { new(-48, -48, 0), new(48, -48, 0), new(48, 48, 0), new(-48, 48, 0), new(-48, -12, 0), new(3, -12, 0), new(3, -18, 0), new(-48, -18, 0) };
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = false, DistanceToHeadland = 88 };
        var r = creation.CreateTurnPath(new Position { Easting = 0, Northing = -40, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, boundary, turnLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 8, headlandDistance: 88);

        Assert.That(r.Path, Is.Not.Null, "a turn is built");
        Assert.That(r.Path!.Max(p => p.Northing), Is.GreaterThan(40), "at the north end");
        Assert.That(r.Path.Min(p => p.Northing), Is.GreaterThan(0), "not at the notch");
    }

    [Test]
    public void A_turn_seated_back_behind_the_tractor_to_clear_a_hard_boundary_is_refused()
    {
        // The 16 m sprayer against a hard fence (AutoTurnHardBoundaryTests): the turn is seated
        // back to keep the boom inside. Made from right at the headland, where the seating puts
        // the arc start behind the tractor, there is no turn; made in time, the arc starts ahead.
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        c.Tool.Width = 6;
        c.Tool.Overlap = 0;
        // A fixed tool's working point 4.5 m from the rear axle: the tractor's hitch there, the tool on it (#547).
        c.Vehicle.ThreePointHitchLength = c.Vehicle.FrontHitchLength = System.Math.Abs(4.5);
        c.Tool.WorkingPointFromHitch = 0;
        c.Tool.Length = 3.5;
        c.Tool.IsToolRearFixed = true;
        c.Tool.IsToolTrailing = false;
        c.Tool.IsToolTBT = false;
        c.Tool.IsToolFrontFixed = false;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, 1600);
        c.Guidance.UTurnDistanceFromBoundary = 1.0;
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnSmoothing = 3;
        var creation = new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);

        var fence = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 50), new(-50, 50) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
                IsHard = true,
            },
        };
        var square = new List<Vec3> { new(-40, -40, 0), new(40, -40, 0), new(40, 40, 0), new(-40, 40, 0) };
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

        YouTurnCreationService.TurnPathResult Run(double northing)
        {
            var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
            var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = 40 - northing };
            return creation.CreateTurnPath(new Position { Easting = 0, Northing = northing, Heading = 0 }, track,
                headingRadians: 0, abHeading: 0, boundary, square, guidance, turn,
                uTurnSkipRows: 0, headlandCalculatedWidth: 10, headlandDistance: 40 - northing);
        }

        var inTime = Run(20);
        Assert.That(inTime.Path, Is.Not.Null, "made 20 m before the headland, the seated turn is built");
        Assert.That(inTime.ArcStart, Is.Not.Null);
        double arcAhead = inTime.ArcStart!.Value.Northing - 20;
        Assert.That(arcAhead, Is.GreaterThanOrEqualTo(2), "the arc starts ahead of the tractor");
        TestContext.Out.WriteLine($"arc start {arcAhead:F1} m ahead of the tractor at y = 20");

        // From where the seated arc would start less than 2 m ahead: refused.
        double late = 20 + arcAhead - 1;
        var tooLate = Run(late);
        Assert.That(tooLate.Path, Is.Null, $"made at y = {late:F1}, the seated arc would start behind the tractor");
        Assert.That(tooLate.ClearanceBlocked, Is.True);
    }
}
