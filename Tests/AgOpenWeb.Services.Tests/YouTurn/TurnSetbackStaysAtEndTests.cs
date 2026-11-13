// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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
/// A turn tried again with the turn line moved in, to keep the implement off a hard fence, is
/// seated back from the end of the pass the first try found. Moved in, the fence's turn line also
/// crossed the pass where the fence comes in beside it, and the first crossing ahead was that
/// one: on a real field a front 8 m boom near the fence at the far end moved the turn line in
/// 5 m, the turn was made 260 m before the end, beside the narrowing, and landed on the next pass
/// 16 m after its edge entered the crop, which was never sprayed. A rear-mounted implement
/// gives the same.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton.
public class TurnSetbackStaysAtEndTests
{
    private const double FenceNorth = 100;

    // The east fence comes in to x = 9 from y = -15 to 15, with 45° sides: the pass x = 4 runs
    // past it, the implement's east end (x = 8) a metre inside.
    private static List<Vec2> Fence(bool bulge) => bulge
        ? new() { new(-50, -100), new(50, -100), new(50, -56), new(9, -15), new(9, 15), new(50, 56), new(50, FenceNorth), new(-50, FenceNorth) }
        : new() { new(-50, -100), new(50, -100), new(50, FenceNorth), new(-50, FenceNorth) };

    private static YouTurnCreationService NewCreation(bool front)
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var c = ConfigurationStore.Instance;
        // A front sprayer: an 8 m boom fixed 4.5 m ahead of the pivot; or an 8 m implement fixed
        // 1.5 m behind it.
        const double width = 8;
        c.Vehicle.Wheelbase = 2.9;
        c.Tool.Width = width;
        c.Tool.Overlap = 0;
        c.Tool.Offset = 0;
        c.Tool.HitchLength = front ? 4.5 : 1.5;
        c.Tool.IsToolFrontFixed = front;
        c.Tool.IsToolRearFixed = !front;
        c.Tool.IsToolTrailing = false;
        c.Tool.IsToolTBT = false;
        c.NumSections = 1;
        c.Tool.SetSectionWidth(0, (int)(width * 100));
        c.Guidance.UTurnDistanceFromBoundary = 2.0;
        c.Guidance.UTurnRadius = 8.0;
        c.Guidance.UTurnExtension = 20.0;
        c.Guidance.UTurnStyle = 0;
        return new YouTurnCreationService(NullLogger<YouTurnCreationService>.Instance, new PolygonOffsetService(), c);
    }

    private static Boundary Field(bool bulge, bool hard) => new()
    {
        OuterBoundary = new BoundaryPolygon
        {
            Points = Fence(bulge).Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList(),
            IsHard = hard,
        },
    };

    /// <summary>The fence moved in by <paramref name="d"/>: the x where it crosses y = 0.</summary>
    private static double BulgeTurnLineX(double d)
    {
        var line = new PolygonOffsetService().CreateInwardOffset(Fence(bulge: true), d)!;
        double best = double.MaxValue;
        for (int i = 0; i < line.Count; i++)
        {
            var a = line[i];
            var b = line[(i + 1) % line.Count];
            if ((a.Northing - 0) * (b.Northing - 0) > 0 || a.Northing == b.Northing) continue;
            double x = a.Easting + (0 - a.Northing) / (b.Northing - a.Northing) * (b.Easting - a.Easting);
            if (x > 0 && x < best) best = x;
        }
        return best;
    }

    private static YouTurnCreationService.TurnPathResult TurnFromTheSouth(YouTurnCreationService creation, Boundary field)
    {
        // Pass 0 (x = 4) north from y = -70, the next pass (x = -4) to the left.
        const double y = -70;
        var turnLine = new PolygonOffsetService().CreateInwardOffset(
            field.OuterBoundary!.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList(), 2.0)!
            .Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList();
        var track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -200, 0), new Vec3(0, 200, 0));
        var guidance = new GuidanceWorkingState { IsHeadingSameWay = true, HowManyPathsAway = 0 };
        var turn = new YouTurnWorkingState { IsTurnLeft = true, DistanceToHeadland = FenceNorth - 2 - y };
        return creation.CreateTurnPath(new Position { Easting = 4, Northing = y, Heading = 0 }, track,
            headingRadians: 0, abHeading: 0, field, turnLine, guidance, turn,
            uTurnSkipRows: 0, headlandCalculatedWidth: 12, headlandDistance: FenceNorth - 2 - y);
    }

    [TestCase(true, TestName = "A turn seated back from a hard fence stays at the end of the pass (front boom)")]
    [TestCase(false, TestName = "A turn seated back from a hard fence stays at the end of the pass (rear implement)")]
    public void A_turn_seated_back_from_a_hard_fence_stays_at_the_end_of_the_pass(bool front)
    {
        Assert.Multiple(() =>
        {
            Assert.That(BulgeTurnLineX(2.0), Is.GreaterThan(4.5), "the scenario: the turn line passes the pass by");
            Assert.That(BulgeTurnLineX(6.5), Is.LessThan(4.0), "the scenario: moved in 4.5 m more, it crosses the pass");
        });

        // Without the hard fence the turn is made at the end, against the turn line; with it the
        // implement swinging round is kept off the fence by seating the turn back.
        var soft = TurnFromTheSouth(NewCreation(front), Field(bulge: true, hard: false));
        var plain = TurnFromTheSouth(NewCreation(front), Field(bulge: false, hard: true));
        var turn = TurnFromTheSouth(NewCreation(front), Field(bulge: true, hard: true));

        Assert.That(soft.Path, Is.Not.Null);
        Assert.That(plain.Path, Is.Not.Null);
        double softTop = soft.Path!.Max(p => p.Northing);
        double plainTop = plain.Path!.Max(p => p.Northing);
        TestContext.Out.WriteLine($"top of the turn: soft {softTop:F1}, hard {plainTop:F1}");
        Assert.That(plainTop, Is.LessThan(softTop - 1), "the scenario: the hard fence seats the turn back");

        Assert.That(turn.Path, Is.Not.Null, "a turn");
        double top = turn.Path!.Max(p => p.Northing);
        TestContext.Out.WriteLine($"top of the turn beside the bulge: {top:F1}, arc start {turn.ArcStart?.Northing:F1}");
        Assert.Multiple(() =>
        {
            Assert.That(top, Is.EqualTo(plainTop).Within(0.5), "the same turn as without the bulge, at the end of the pass");
            Assert.That(turn.ArcStart!.Value.Northing, Is.GreaterThan(50), "not beside the bulge");
        });
    }
}
