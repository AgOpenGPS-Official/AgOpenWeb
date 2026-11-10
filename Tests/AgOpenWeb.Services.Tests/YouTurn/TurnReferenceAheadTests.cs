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
        var r = YouTurnCreationService.FindTrackHeadlandIntersectionAhead(Track, new Vec3(0, -35, 0), Headland, headingSameWay: true);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.Northing, Is.EqualTo(-27).Within(1e-6));

        // Heading south at y = -35: the crossing ahead is y = -40.
        r = YouTurnCreationService.FindTrackHeadlandIntersectionAhead(Track, new Vec3(0, -35, 0), Headland, headingSameWay: false);
        Assert.That(r!.Value.Northing, Is.EqualTo(-40).Within(1e-6));
    }

    [Test]
    public void Past_the_last_crossing_the_one_behind_is_kept()
    {
        // Heading north at y = 45, past the north edge: nothing ahead, the edge behind as before.
        var r = YouTurnCreationService.FindTrackHeadlandIntersectionAhead(Track, new Vec3(0, 45, 0), Headland, headingSameWay: true);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Value.Northing, Is.EqualTo(40).Within(1e-6));
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
        c.Tool.HitchLength = 4.5;
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
