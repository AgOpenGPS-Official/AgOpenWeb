// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.YouTurn;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// A turn refused for want of room on the next pass says why, with what was needed and what was
/// there, as the other refusals do. Field jobs J9 and J18 on Olival (2026-11-16): the next pass is
/// inside the turn line for 15 m (5 m) at the end, then runs 1.0 m (0.1 m) from the fence, where
/// no tractor fits, then on through the field; AgOpenWeb said "End of field reached - no room on
/// the next pass at this end" with 16 % of the field left, and nothing said why.
/// </summary>
[TestFixture, NonParallelizable]
public class NoLandingMessageTests
{
    private ConfigurationStore _c = null!;
    private YouTurnPathingService _pathing = null!;
    private readonly AgOpenWeb.Models.Track.Track _track = AgOpenWeb.Models.Track.Track.FromABLine("AB", new Vec3(0, -100, 0), new Vec3(0, 100, 0));

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _c = ConfigurationStore.Instance;
        _c.Tool.Overlap = 0;
        _c.NumSections = 1;
        _c.Tool.SetSectionWidth(0, 800);
        _c.Guidance.UTurnDistanceFromBoundary = 2.0;
        _pathing = new YouTurnPathingService(NullLogger<YouTurnPathingService>.Instance, _c);
    }

    private double PassX(int k) => AgOpenWeb.Models.Track.PassGeometry.Offset(_track, k, 8.0);

    // The square ±50 with the fence on the east side coming in to 1 m from pass 1 between y = 20
    // and 30; the turn line is the fence moved in 2 m. Pass 1 is inside the turn line from the
    // north end (48) back to 32, outside it, 1 m from the fence, to 18, inside again from there.
    private (Boundary Fence, List<Vec3> TurnLine) Field(double fenceX)
    {
        var fence = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 20), new(fenceX, 20), new(fenceX, 30), new(50, 30), new(50, 50), new(-50, 50) };
        var turnLine = new List<Vec3>
        {
            new(-48, -48, 0), new(48, -48, 0), new(48, 18, 0), new(fenceX - 2, 18, 0), new(fenceX - 2, 32, 0),
            new(48, 32, 0), new(48, 48, 0), new(-48, 48, 0),
        };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        return (boundary, turnLine);
    }

    [Test]
    public void The_next_pass_along_the_fence_nearer_than_the_U_turn_distance_says_how_near_and_how_much_is_needed()
    {
        var (fence, turnLine) = Field(PassX(1) + 1);
        var at = new Position { Easting = PassX(0), Northing = 0, Heading = 0 };

        var run = _pathing.Landing(_track, 0, 1, fence, turnLine, at, headingSameWay: true, headlandAhead: 48, stopAt: 20);
        Assert.That(run.Length, Is.EqualTo(16).Within(0.5), "the piece at the end");
        Assert.That(run.FenceGap, Is.EqualTo(1.0).Within(0.01), "then 1 m from the fence");

        var message = _pathing.NoLandingMessage(_track, 0, 1, fence, turnLine, at, headingSameWay: true, headlandAhead: 48, fromPath: 0);
        Assert.That(message, Is.EqualTo("No U-turn onto the next pass: 16 m in, it runs 1.0 m from the boundary, 2.0 m needed - take over manually at the end of this pass"));
    }

    [Test]
    public void The_next_pass_cut_short_by_the_field_says_how_long_it_is_and_how_much_is_needed()
    {
        // The same piece, the fence closing over pass 1 below it (no way back into the field).
        var (_, turnLine) = Field(PassX(1) + 1);
        var fence = new List<Vec2> { new(-50, -50), new(50, -50), new(50, 20), new(PassX(1) - 1, 20), new(PassX(1) - 1, 30), new(50, 30), new(50, 50), new(-50, 50) };
        var boundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon { Points = fence.Select(p => new BoundaryPoint(p.Easting, p.Northing, 0)).ToList() },
        };
        var at = new Position { Easting = PassX(0), Northing = 0, Heading = 0 };

        var message = _pathing.NoLandingMessage(_track, 0, 1, boundary, turnLine, at, headingSameWay: true, headlandAhead: 48, fromPath: 0);
        Assert.That(message, Is.EqualTo("No U-turn onto the next pass: only 16 m of it at this end, 20 m needed - take over manually at the end of this pass"));
    }

    [Test]
    public void A_pass_with_room_has_no_message()
    {
        var (fence, turnLine) = Field(PassX(1) + 1);
        var at = new Position { Easting = PassX(-1), Northing = 0, Heading = 0 };
        // Pass -2 runs clear of the notch: 96 m inside the turn line.
        Assert.That(_pathing.NoLandingMessage(_track, 0, -2, fence, turnLine, at, headingSameWay: true, headlandAhead: 48, fromPath: -1), Is.Null);
    }
}
